﻿using Newtonsoft.Json.Linq;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.Entity;
using OsEngine.Market.Servers.FTX.EntityCreators;
using OsEngine.Market.Servers.FTX.FtxApi;
using OsEngine.Market.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.FTX
{
    public class FTXServer : AServer
    {
        public FTXServer()
        {
            FTXServerRealization realization = new FTXServerRealization();
            ServerRealization = realization;

            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamSecretKey, "");
        }

        /// <summary>
        /// instrument history query
        /// запрос истории по инструменту
        /// </summary>
        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            return ((FTXServerRealization)ServerRealization).GetCandleHistory(nameSec, tf);
        }
    }

    public class FTXServerRealization : AServerRealization
    {
        #region private constants
        private const int CandlesDownloadLimit = 5000;
        private const int TradesDownloadLimit = 100;
        private const string WebSocketEndpointUrl = "wss://ftx.com/ws/";
        #endregion

        #region private fields

        /// <summary>
        /// Available timeframes in FTX.
        /// словарь таймфреймов, поддерживаемых этой биржей
        /// </summary>
        private readonly Dictionary<int, string> _supportedIntervals = new Dictionary<int, string>
        {
            { 15, "15sec" },
            { 60, "1min" },
            { 300, "5min" },
            { 900, "15min" },
            { 3600, "1hour" },
            { 14400, "4hour" },
            { 86400, "1day" }
        };

        private WsSource _wsSource;

        private CancellationTokenSource _cancelTokenSource;

        private readonly ConcurrentQueue<string> _queueMessagesReceivedFromExchange = new ConcurrentQueue<string>();
        private readonly Dictionary<string, Action<JToken>> _responseHandlers;

        private FTXSecurityCreator _securitiesCreator;
        private FTXPortfolioCreator _portfoliosCreator;
        private FTXMarketDepthCreator _marketDepthCreator;
        private FTXTradesCreator _tradesCreator;
        private FTXOrderCreator _orderCreator;
        private FTXCandlesCreator _candlesCreator;
        private DateTime _lastTimeUpdateSocket;

        private bool _isPortfolioSubscribed = false;
        private bool _loginFailed = false;

        private FtxRestApi _ftxRestApi;

        private Client _client;

        private readonly List<string> _subscribedSecurities = new List<string>();
        #endregion

        #region public properties
        public override ServerType ServerType => ServerType.FTX;
        #endregion

        #region private methods
        private void WsSourceMessageEvent(WsMessageType msgType, string message)
        {
            switch (msgType)
            {
                case WsMessageType.Opened:
                    SendLoginMessage();
                    OnConnectEvent();
                    break;
                case WsMessageType.Closed:
                    OnDisconnectEvent();
                    break;
                case WsMessageType.StringData:
                    _queueMessagesReceivedFromExchange.Enqueue(message);
                    break;
                case WsMessageType.Error:
                    SendLogMessage(message, LogMessageType.Error);
                    break;
                default:
                    throw new NotSupportedException(message);
            }
        }

        private void StartMessageReader()
        {
            Task.Run(() => MessageReader(_cancelTokenSource.Token), _cancelTokenSource.Token);
            Task.Run(() => SourceAliveCheckerThread(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        private async void MessageReader(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_queueMessagesReceivedFromExchange.IsEmpty &&
                        _queueMessagesReceivedFromExchange.TryDequeue(out string mes))
                    {
                        var response = JToken.Parse(mes);
                        var type = response.SelectToken("type").ToString();
                        if (_responseHandlers.ContainsKey(type))
                        {
                            _responseHandlers[type].Invoke(response);
                        }
                        else
                        {
                            SendLogMessage(mes, LogMessageType.System);
                        }
                    }
                    else
                    {
                        await Task.Delay(20);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }


        private async void SourceAliveCheckerThread(CancellationToken token)
        {
            var pingMessage = FtxWebSockerRequestGenerator.GetPingRequest();
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(15000);
                _wsSource?.SendMessage(pingMessage);

                if (_lastTimeUpdateSocket == DateTime.MinValue)
                {
                    continue;
                }
                if (_lastTimeUpdateSocket.AddSeconds(60) < DateTime.Now)
                {
                    SendLogMessage("The websocket is disabled. Restart", LogMessageType.Error);
                    Dispose();
                    OnDisconnectEvent();
                    return;
                }
            }
        }

        private void SendLoginMessage()
        {
            var loginMessage = FtxWebSockerRequestGenerator.GetAuthRequest(_client);
            _wsSource.SendMessage(loginMessage);
        }

        private List<Candle> GetCandles(int oldInterval, string securityName, DateTime startTime, DateTime endTime)
        {
            if(oldInterval < _supportedIntervals.First().Key)
            {
                return null;
            }

            List<Candle> candles = new List<Candle>();
            
            var needIntervalForQuery =
                CandlesCreator.DetermineAppropriateIntervalForRequest(oldInterval, _supportedIntervals,
                    out var needInterval);

            var step = new TimeSpan(0, 0, (int)(needInterval * CandlesDownloadLimit));

            var actualTime = startTime;

            var midTime = actualTime + step;

            while (true)
            {
                if (actualTime >= endTime)
                {
                    break;
                }

                if (midTime > endTime)
                {
                    midTime = endTime;
                }

                var histaricalPrices = _ftxRestApi.GetHistoricalPricesAsync(securityName, needInterval, CandlesDownloadLimit, actualTime, midTime).Result;

                List<Candle> newCandles = _candlesCreator.Create(histaricalPrices);

                if (newCandles != null && newCandles.Count != 0)
                    candles.AddRange(newCandles);

                actualTime = candles[candles.Count - 1].TimeStart.AddSeconds(needInterval);
                midTime = actualTime + step;
                Thread.Sleep(1000);
            }

            if (candles.Count == 0)
            {
                return null;
            }

            if (oldInterval == needInterval)
            {
                return candles;
            }

            var requiredIntervalCandles =
                CandlesCreator.CreateCandlesRequiredInterval(needInterval, oldInterval, candles);

            return requiredIntervalCandles;
        }

        #region message handlers
        private void HandlePongMessage(JToken response)
        {
            _lastTimeUpdateSocket = DateTime.Now;
        }

        private void HandleErrorMessage(JToken response)
        {
            var errorMessage = response.SelectToken("msg").ToString();
            if(errorMessage == "Already subscribed")
            {
                return;
            }
            SendLogMessage(errorMessage, LogMessageType.Error);
        }

        private void HandleSubscribedMessage(JToken response)
        {
            var channel = response.SelectToken("channel").ToString();
            switch (channel)
            {
                case "trades":
                    var securityName = response.SelectToken("market").ToString();
                    _subscribedSecurities.Add(securityName);
                    break;
                case "fills":
                    _isPortfolioSubscribed = true;
                    break;
                default:
                    break;
            }      
        }

        private void HandleUnsubscribedMessage(JToken response)
        {
            var channel = response.SelectToken("channel").ToString();
            switch (channel)
            {
                case "trades":
                    var securityName = response.SelectToken("market").ToString();
                    _subscribedSecurities.Remove(securityName);
                    break;
                case "fills":
                    _isPortfolioSubscribed = false;
                    break;
                default:
                    break;
            }
        }

        private void HandleInfoMessage(JToken response)
        {
            SendLogMessage(response.ToString(), LogMessageType.NoName);
        }

        private void HandlePartialMessage(JToken response)
        {
            var channel = response.SelectToken("channel").ToString();
            switch (channel)
            {
                case "orderbook":
                    OnMarketDepthEvent(_marketDepthCreator.Create(response));
                    break;
                case "trades":
                    OnTradeEvent(_tradesCreator.Create(response));
                    break;
                case "ticker":
                    break;
                case "fills":
                    break;
                case "orders":
                    break;
                default:
                    SendLogMessage("Unhandeled channel :" + channel, LogMessageType.System);
                    break;
            }
        }

        private void HandleUpdateMessage(JToken response)
        {
            var channel = response.SelectToken("channel").ToString();
            switch (channel)
            {
                case "orderbook":
                    OnMarketDepthEvent(_marketDepthCreator.Update(response));
                    break;
                case "trades":
                    OnTradeEvent(_tradesCreator.Create(response));
                    break;
                case "ticker":
                    break;
                case "fills":
                    OnMyTradeEvent(_tradesCreator.CreateMyTrade(response));
                    break;
                case "orders":
                    var order = _orderCreator.Create(response);
                    if(order.State == OrderStateType.Done)
                    {
                        _orderCreator.RemoveMyOrder(order);
                    }
                    OnOrderEvent(order);
                    break;
                default:
                    SendLogMessage("Unhandeled channel :" + channel, LogMessageType.System);
                    break;
            }
        }
        #endregion
        #endregion

        #region public methods
        public FTXServerRealization() : base()
        {
            _responseHandlers = new Dictionary<string, Action<JToken>>();
            _responseHandlers.Add("pong", HandlePongMessage);
            _responseHandlers.Add("error", HandleErrorMessage);
            _responseHandlers.Add("subscribed", HandleSubscribedMessage);
            _responseHandlers.Add("unsubscribed", HandleUnsubscribedMessage);
            _responseHandlers.Add("info", HandleInfoMessage);
            _responseHandlers.Add("partial", HandlePartialMessage);
            _responseHandlers.Add("update", HandleUpdateMessage);
        }

        public override async void CancelOrder(Order order)
        {
            var cancelOrderResponse = await _ftxRestApi.CancelOrderAsync(order.NumberMarket);

            var isSuccessful = cancelOrderResponse.SelectToken("success").Value<bool>();

            if (isSuccessful)
            {
                SendLogMessage($"Order num {order.NumberUser} canceled.", LogMessageType.Trade);
                order.State = OrderStateType.Cancel;
                _orderCreator.RemoveMyOrder(order);
                OnOrderEvent(order);
            }
            else
            {
                string errorMsg = cancelOrderResponse.SelectToken("error").ToString();

                SendLogMessage($"Error on order cancel num {order.NumberUser} : {errorMsg}", LogMessageType.Error);
            }
        }

        public override void Connect()
        {
            _isPortfolioSubscribed = false;
            _loginFailed = false;

            _securitiesCreator = new FTXSecurityCreator();
            _portfoliosCreator = new FTXPortfolioCreator();
            _marketDepthCreator = new FTXMarketDepthCreator();
            _tradesCreator = new FTXTradesCreator();
            _orderCreator = new FTXOrderCreator();
            _candlesCreator = new FTXCandlesCreator();

            _client = new Client(
                ((ServerParameterString)ServerParameters[0]).Value,
                ((ServerParameterPassword)ServerParameters[1]).Value);

            _cancelTokenSource = new CancellationTokenSource();
            _ftxRestApi = new FtxRestApi(_client);

            StartMessageReader();

            _wsSource = new WsSource(WebSocketEndpointUrl);
            _wsSource.MessageEvent += WsSourceMessageEvent;
            _wsSource.Start();
        }

        public override void Dispose()
        {
            try
            {
                if(_wsSource != null)
                {
                    if (_isPortfolioSubscribed)
                    {
                        var fillsRequest = FtxWebSockerRequestGenerator.GetUnsubscribeRequest("fills");
                        _wsSource.SendMessage(fillsRequest);

                        var ordersRequest = FtxWebSockerRequestGenerator.GetUnsubscribeRequest("orders");
                        _wsSource.SendMessage(ordersRequest);

                        _isPortfolioSubscribed = false;
                    }

                    if (_subscribedSecurities.Any())
                    {
                        foreach(var security in _subscribedSecurities)
                        {
                            var unsubscribeMarket = FtxWebSockerRequestGenerator.GetUnsubscribeRequest("market", security);
                            _wsSource.SendMessage(unsubscribeMarket);

                            var unsubscribeOrderbook = FtxWebSockerRequestGenerator.GetUnsubscribeRequest("orderbook", security);
                            _wsSource.SendMessage(unsubscribeOrderbook);
                        }
                        _subscribedSecurities.Clear();
                    }

                    _wsSource.Dispose();
                    _wsSource.MessageEvent -= WsSourceMessageEvent;
                    _wsSource = null;

                    _securitiesCreator = null;
                    _portfoliosCreator = null;
                    _marketDepthCreator = null;
                    _tradesCreator = null;
                    _orderCreator = null;
                    _candlesCreator = null;

                    _client = null;
                    _ftxRestApi = null;
                }

                if (_cancelTokenSource != null && !_cancelTokenSource.IsCancellationRequested)
                {
                    _cancelTokenSource.Cancel();
                }

            }
            catch (Exception e)
            {
                SendLogMessage("FTX dispose error: " + e, LogMessageType.Error);
            }
        }

        public override List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            var trades = new List<Trade>();

            if(startTime > actualTime)
            {
                actualTime = startTime;
            }

            var lastTradeTime = endTime;

            while(lastTradeTime > actualTime)
            {
                var marketTradesResponse = _ftxRestApi.GetMarketTradesAsync(security.Name, TradesDownloadLimit, actualTime, lastTradeTime).Result;

                var newTrades = _tradesCreator.Create(marketTradesResponse, security.Name);

                if(newTrades != null && newTrades.Any())
                {
                    trades.InsertRange(0, newTrades);
                }
                else
                {
                    break;
                }

                lastTradeTime = trades.First().Time;
                Thread.Sleep(100);
            }

            return trades;
        }

        public override List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            int oldInterval = Convert.ToInt32(timeFrameBuilder.TimeFrameTimeSpan.TotalSeconds);
            return GetCandles(oldInterval, security.Name, startTime, endTime);
        }


        public List<Candle> GetCandleHistory(string nameSec, TimeSpan interval)
        {
            int oldInterval = Convert.ToInt32(interval.TotalSeconds);
            var diff = new TimeSpan(0, (int)(interval.TotalMinutes * CandlesDownloadLimit), 0);
            return GetCandles(oldInterval, nameSec, DateTime.Now - diff, DateTime.Now);
        }

        public override void GetOrdersState(List<Order> orders)
        {
        }

        public async override void GetPortfolios()
        {
            if (!_isPortfolioSubscribed && !_loginFailed)
            {
                var accountResponse = await _ftxRestApi.GetAccountInfoAsync();

                var isSuccessfull = accountResponse.SelectToken("success").Value<bool>();
                var portfolios = new List<Portfolio>();
                if (isSuccessfull)
                {
                    var fillsRequest = FtxWebSockerRequestGenerator.GetSubscribeRequest("fills");
                    _wsSource.SendMessage(fillsRequest);

                    var ordersRequest = FtxWebSockerRequestGenerator.GetSubscribeRequest("orders");
                    _wsSource.SendMessage(ordersRequest);

                    portfolios.Add(_portfoliosCreator.Create(accountResponse, "main"));
                }
                else
                {
                    _loginFailed = true;
                    SendLogMessage($"Can not get portfolios info.", LogMessageType.Error);
                    portfolios.Add(_portfoliosCreator.Create("undefined"));
                }
                OnPortfolioEvent(portfolios);
            }
        }

        public async override void GetSecurities()
        {
            var marketsResponse = await _ftxRestApi.GetMarketsAsync();
            OnSecurityEvent(_securitiesCreator.Create(marketsResponse));
        }

        public async override void SendOrder(Order order)
        {
            if(order.TypeOrder == OrderPriceType.Iceberg)
            {
                SendLogMessage("FTX does't support iceberg orders", LogMessageType.Error);
                return;
            }

            var placeOrderResponse = await _ftxRestApi.PlaceOrderAsync(order.SecurityNameCode, order.Side, order.Price, order.TypeOrder, order.Volume);

            var isSuccessful = placeOrderResponse.SelectToken("success").Value<bool>();
            if (isSuccessful)
            {
                SendLogMessage($"Order num {order.NumberUser} on exchange.", LogMessageType.Trade);
                _orderCreator.AddMyOrder(order, placeOrderResponse);
            }
            else
            {
                string errorMsg = placeOrderResponse.SelectToken("error").ToString();
                SendLogMessage($"Order exchange error num {order.NumberUser} : {errorMsg}", LogMessageType.Error);
                order.State = OrderStateType.Fail;

                OnOrderEvent(order);
            }
        }

        public override void Subscrible(Security security)
        {
           if(!_subscribedSecurities.Contains(security.Name))
            {
                var subscribeMarket = FtxWebSockerRequestGenerator.GetSubscribeRequest("trades", security.Name);
                _wsSource.SendMessage(subscribeMarket);

                var subscribeOrderbook = FtxWebSockerRequestGenerator.GetSubscribeRequest("orderbook", security.Name);
                _wsSource.SendMessage(subscribeOrderbook);
            }
        }
        #endregion
    }
}
