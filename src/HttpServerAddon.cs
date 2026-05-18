//
// NinjaTrader 8 - HTTP Server Add-On COMPLET
// Compatible avec ozmnf4/ninjatrader-mcp
// Port : 7890
//

#region Using declarations
using System;
using System.Net;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class HttpServerAddon : AddOnBase
    {
        private HttpListener httpListener;
        private Thread       listenerThread;
        private bool         isRunning = false;

        #region Properties
        [Display(Name = "Port du serveur HTTP",
                 Description = "Port HTTP (défaut : 7890)",
                 Order = 1, GroupName = "Paramètres HTTP Server")]
        public int ServerPort { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "HTTP Server Add-On";
                Description = "Serveur HTTP local port 7890 - Compatible ozmnf4/ninjatrader-mcp";
                ServerPort  = 7890;
            }
            else if (State == State.Active)    { StartHttpServer(); }
            else if (State == State.Terminated) { StopHttpServer(); }
        }

        private void StartHttpServer()
        {
            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add(string.Format("http://localhost:{0}/",  ServerPort));
                httpListener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", ServerPort));
                httpListener.Start();
                isRunning = true;
                listenerThread              = new Thread(ListenForRequests);
                listenerThread.IsBackground = true;
                listenerThread.Name         = "HttpServerAddon_Listener";
                listenerThread.Start();
                Log(string.Format("[HTTP Server] Démarré sur http://localhost:{0}", ServerPort),
                    NinjaTrader.Cbi.LogLevel.Information);
            }
            catch (Exception ex)
            {
                Log(string.Format("[HTTP Server] Erreur : {0}", ex.Message),
                    NinjaTrader.Cbi.LogLevel.Error);
            }
        }

        private void StopHttpServer()
        {
            isRunning = false;
            try
            {
                if (httpListener != null && httpListener.IsListening)
                { httpListener.Stop(); httpListener.Close(); }
            }
            catch { }
            Log("[HTTP Server] Arrêté.", NinjaTrader.Cbi.LogLevel.Information);
        }

        private void ListenForRequests()
        {
            while (isRunning)
            {
                try
                {
                    if (httpListener == null || !httpListener.IsListening) break;
                    HttpListenerContext ctx = httpListener.GetContext();
                    Task.Run(() => ProcessRequest(ctx));
                }
                catch (HttpListenerException)   { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log(string.Format("[HTTP Server] Listener : {0}", ex.Message),
                        NinjaTrader.Cbi.LogLevel.Warning);
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            HttpListenerRequest  req = context.Request;
            HttpListenerResponse res = context.Response;

            res.Headers.Add("Access-Control-Allow-Origin",  "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            res.ContentType = "application/json; charset=utf-8";

            string json = "{}";
            string path = req.Url.AbsolutePath.ToLower().TrimEnd('/');
            string[] segments = path.Split(new char[]{'/'}, StringSplitOptions.RemoveEmptyEntries);

            string body = "";
            if (req.HttpMethod == "POST" && req.HasEntityBody)
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = reader.ReadToEnd();

            try
            {
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 200; json = "{}";
                }

                // ── HEALTH ────────────────────────────────────────────────
                else if (path == "/health" || path == "/status")
                {
                    json = string.Format(
                        "{{\"status\":\"ok\",\"port\":{0},\"nt_version\":\"8.0\",\"timestamp\":\"{1}\"}}",
                        ServerPort, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                }

                // ── ACCOUNTS ──────────────────────────────────────────────
                // GET /accounts
                else if (path == "/accounts" && req.HttpMethod == "GET")
                {
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (Account acc in Account.All)
                    {
                        if (!first) sb.Append(",");
                        string connName = "N/A";
                        if (acc.Connection != null && acc.Connection.Options != null)
                            connName = acc.Connection.Options.Name;
                        sb.AppendFormat(
                            "{{\"name\":\"{0}\",\"connection\":\"{1}\",\"status\":\"{2}\"}}",
                            J(acc.Name), J(connName), acc.ConnectionStatus.ToString());
                        first = false;
                    }
                    sb.Append("]");
                    json = string.Format("{{\"accounts\":{0}}}", sb);
                }

                // GET /account/{name}
                else if (segments.Length == 2 && segments[0] == "account" && req.HttpMethod == "GET")
                {
                    string accName = Uri.UnescapeDataString(segments[1]);
                    Account found  = null;
                    foreach (Account acc in Account.All)
                        if (acc.Name.Equals(accName, StringComparison.OrdinalIgnoreCase)) { found = acc; break; }

                    if (found == null)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Compte '{0}' introuvable\"}}", J(accName)); }
                    else
                    {
                        string connName = "N/A";
                        if (found.Connection != null && found.Connection.Options != null)
                            connName = found.Connection.Options.Name;
                        json = string.Format(
                            "{{\"name\":\"{0}\",\"connection\":\"{1}\",\"status\":\"{2}\",\"buyingPower\":{3},\"cashValue\":{4}}}",
                            J(found.Name), J(connName),
                            found.ConnectionStatus.ToString(),
                            found.Get(AccountItem.BuyingPower, Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            found.Get(AccountItem.CashValue,   Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                // GET /balance/{name}
                else if (segments.Length == 2 && segments[0] == "balance" && req.HttpMethod == "GET")
                {
                    string accName = Uri.UnescapeDataString(segments[1]);
                    Account found  = null;
                    foreach (Account acc in Account.All)
                        if (acc.Name.Equals(accName, StringComparison.OrdinalIgnoreCase)) { found = acc; break; }

                    if (found == null)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Compte '{0}' introuvable\"}}", J(accName)); }
                    else
                    {
                        json = string.Format(
                            "{{\"account\":\"{0}\",\"cashValue\":{1},\"buyingPower\":{2},\"realizedPnL\":{3},\"unrealizedPnL\":{4},\"netLiquidation\":{5}}}",
                            J(found.Name),
                            found.Get(AccountItem.CashValue,             Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            found.Get(AccountItem.BuyingPower,           Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            found.Get(AccountItem.RealizedProfitLoss,    Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            found.Get(AccountItem.UnrealizedProfitLoss,  Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            found.Get(AccountItem.NetLiquidation,        Currency.UsDollar).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                // ── POSITIONS ─────────────────────────────────────────────
                // GET /positions
                else if (path == "/positions" && req.HttpMethod == "GET")
                {
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (Account acc in Account.All)
                        foreach (Position pos in acc.Positions)
                        {
                            if (!first) sb.Append(",");
                            sb.AppendFormat(
                                "{{\"account\":\"{0}\",\"instrument\":\"{1}\",\"qty\":{2},\"side\":\"{3}\",\"avgPrice\":{4},\"unrealizedPnL\":{5}}}",
                                J(acc.Name),
                                J(pos.Instrument.FullName),
                                pos.Quantity,
                                pos.MarketPosition.ToString(),
                                pos.AveragePrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency,
                                    pos.Instrument.MasterInstrument.TickSize)
                                   .ToString(System.Globalization.CultureInfo.InvariantCulture));
                            first = false;
                        }
                    sb.Append("]");
                    json = string.Format("{{\"positions\":{0}}}", sb);
                }

                // GET /position/{instrument}
                else if (segments.Length == 2 && segments[0] == "position" && req.HttpMethod == "GET")
                {
                    string instName = Uri.UnescapeDataString(segments[1]).ToUpper();
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (Account acc in Account.All)
                        foreach (Position pos in acc.Positions)
                            if (pos.Instrument.FullName.ToUpper().Contains(instName))
                            {
                                if (!first) sb.Append(",");
                                sb.AppendFormat(
                                    "{{\"account\":\"{0}\",\"instrument\":\"{1}\",\"qty\":{2},\"side\":\"{3}\",\"avgPrice\":{4}}}",
                                    J(acc.Name), J(pos.Instrument.FullName), pos.Quantity,
                                    pos.MarketPosition.ToString(),
                                    pos.AveragePrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                first = false;
                            }
                    sb.Append("]");
                    json = string.Format("{{\"positions\":{0}}}", sb);
                }

                // ── ORDERS ────────────────────────────────────────────────
                // GET /orders
                else if (path == "/orders" && req.HttpMethod == "GET")
                {
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (Account acc in Account.All)
                        foreach (Order ord in acc.Orders)
                        {
                            if (!first) sb.Append(",");
                            sb.AppendFormat(
                                "{{\"account\":\"{0}\",\"id\":\"{1}\",\"instrument\":\"{2}\",\"action\":\"{3}\",\"qty\":{4},\"filled\":{5},\"type\":\"{6}\",\"limitPrice\":{7},\"stopPrice\":{8},\"state\":\"{9}\",\"time\":\"{10}\"}}",
                                J(acc.Name), J(ord.OrderId), J(ord.Instrument.FullName),
                                ord.OrderAction.ToString(), ord.Quantity, ord.Filled,
                                ord.OrderType.ToString(),
                                ord.LimitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ord.StopPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ord.OrderState.ToString(),
                                ord.Time.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                            first = false;
                        }
                    sb.Append("]");
                    json = string.Format("{{\"orders\":{0}}}", sb);
                }

                // POST /order/place
                else if (path == "/order/place" && req.HttpMethod == "POST")
                {
                    string accName   = ExtractJson(body, "account");
                    string instName  = ExtractJson(body, "instrument");
                    string actionStr = ExtractJson(body, "action");
                    string typeStr   = ExtractJson(body, "type");
                    int    qty        = int.Parse(ExtractJsonNum(body, "qty", "1"));
                    double limitPrice = double.Parse(ExtractJsonNum(body, "limitPrice", "0"),
                                            System.Globalization.CultureInfo.InvariantCulture);
                    double stopPrice  = double.Parse(ExtractJsonNum(body, "stopPrice", "0"),
                                            System.Globalization.CultureInfo.InvariantCulture);

                    Account targetAcc = null;
                    foreach (Account acc in Account.All)
                        if (acc.Name.Equals(accName, StringComparison.OrdinalIgnoreCase))
                        { targetAcc = acc; break; }

                    if (targetAcc == null)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Compte '{0}' introuvable\"}}", J(accName)); }
                    else
                    {
                        Instrument inst = Instrument.GetInstrument(instName);
                        if (inst == null)
                        { res.StatusCode = 404; json = string.Format("{{\"error\":\"Instrument '{0}' introuvable\"}}", J(instName)); }
                        else
                        {
                            OrderAction oAction = (OrderAction)Enum.Parse(typeof(OrderAction), actionStr, true);
                            OrderType   oType   = (OrderType)Enum.Parse(typeof(OrderType),   typeStr,   true);
                            Order placed = targetAcc.CreateOrder(inst, oAction, oType,
                                TimeInForce.Day, qty, limitPrice, stopPrice,
                                string.Empty, string.Empty, null);
                            targetAcc.Submit(new Order[]{ placed });
                            json = string.Format(
                                "{{\"success\":true,\"orderId\":\"{0}\",\"state\":\"{1}\"}}",
                                J(placed.OrderId), placed.OrderState.ToString());
                        }
                    }
                }

                // POST /order/modify
                // ✅ CORRECTION : Account.Change() attend IEnumerable<Order>
                // On modifie les propriétés de l'ordre puis on passe une liste
                else if (path == "/order/modify" && req.HttpMethod == "POST")
                {
                    string orderId    = ExtractJson(body, "orderId");
                    int    qty        = int.Parse(ExtractJsonNum(body, "qty", "0"));
                    double limitPrice = double.Parse(ExtractJsonNum(body, "limitPrice", "0"),
                                            System.Globalization.CultureInfo.InvariantCulture);
                    double stopPrice  = double.Parse(ExtractJsonNum(body, "stopPrice",  "0"),
                                            System.Globalization.CultureInfo.InvariantCulture);

                    bool found = false;
                    foreach (Account acc in Account.All)
                        foreach (Order ord in acc.Orders)
                            if (ord.OrderId == orderId && ord.OrderState == OrderState.Working)
                            {
                                // Modifier les propriétés avant d'appeler Change
                                if (qty > 0)        ord.Quantity   = qty;
                                if (limitPrice > 0) ord.LimitPrice = limitPrice;
                                if (stopPrice  > 0) ord.StopPrice  = stopPrice;
                                // ✅ Passer une List<Order> (IEnumerable<Order>)
                                acc.Change(new List<Order> { ord });
                                found = true;
                                json  = string.Format(
                                    "{{\"success\":true,\"orderId\":\"{0}\"}}",
                                    J(orderId));
                                break;
                            }
                    if (!found)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Ordre '{0}' introuvable\"}}", J(orderId)); }
                }

                // POST /order/cancel
                else if (path == "/order/cancel" && req.HttpMethod == "POST")
                {
                    string orderId = ExtractJson(body, "orderId");
                    bool found = false;
                    foreach (Account acc in Account.All)
                        foreach (Order ord in acc.Orders)
                            if (ord.OrderId == orderId && ord.OrderState == OrderState.Working)
                            {
                                acc.Cancel(new List<Order> { ord });
                                found = true;
                                json  = string.Format("{{\"success\":true,\"orderId\":\"{0}\"}}", J(orderId));
                                break;
                            }
                    if (!found)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Ordre '{0}' introuvable\"}}", J(orderId)); }
                }

                // POST /order/cancel-all
                else if (path == "/order/cancel-all" && req.HttpMethod == "POST")
                {
                    string accName = ExtractJson(body, "account");
                    int count = 0;
                    foreach (Account acc in Account.All)
                    {
                        if (!string.IsNullOrEmpty(accName) &&
                            !acc.Name.Equals(accName, StringComparison.OrdinalIgnoreCase)) continue;
                        var toCancel = new List<Order>();
                        foreach (Order ord in acc.Orders)
                            if (ord.OrderState == OrderState.Working) toCancel.Add(ord);
                        if (toCancel.Count > 0) { acc.Cancel(toCancel); count += toCancel.Count; }
                    }
                    json = string.Format("{{\"success\":true,\"cancelledCount\":{0}}}", count);
                }

                // ── MARKET DATA ───────────────────────────────────────────
                // GET /quote/{instrument}
                else if (segments.Length == 2 && segments[0] == "quote" && req.HttpMethod == "GET")
                {
                    string instName = Uri.UnescapeDataString(segments[1]);
                    Instrument inst = Instrument.GetInstrument(instName);
                    if (inst == null)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Instrument '{0}' introuvable\"}}", J(instName)); }
                    else
                    {
                        MarketData md = inst.MarketData;
                        // ✅ CORRECTION : pas de .Change sur MarketDataEventArgs
                        json = string.Format(
                            "{{\"instrument\":\"{0}\",\"last\":{1},\"bid\":{2},\"ask\":{3},\"volume\":{4}}}",
                            J(inst.FullName),
                            md.Last.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            md.Bid.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            md.Ask.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            md.Last.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                // GET /instruments/search?q=NQ
                else if (path == "/instruments/search" && req.HttpMethod == "GET")
                {
                    string q = req.QueryString["q"] ?? "";
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (Instrument inst in Instrument.All)
                        if (q == "" || inst.FullName.ToUpper().Contains(q.ToUpper()))
                        {
                            if (!first) sb.Append(",");
                            // ✅ CORRECTION : Exchange est un objet, utiliser .ToString()
                            string exchStr = "";
                            try { exchStr = inst.MasterInstrument.Exchanges.Count > 0
                                    ? inst.MasterInstrument.Exchanges[0].ToString()
                                    : ""; }
                            catch { exchStr = ""; }
                            sb.AppendFormat(
                                "{{\"name\":\"{0}\",\"type\":\"{1}\",\"exchange\":\"{2}\"}}",
                                J(inst.FullName),
                                inst.MasterInstrument.InstrumentType.ToString(),
                                J(exchStr));
                            first = false;
                        }
                    sb.Append("]");
                    json = string.Format("{{\"instruments\":{0},\"query\":\"{1}\"}}", sb, J(q));
                }

                // GET /bars/{instrument}?period=Minute&value=1&count=100
                else if (segments.Length == 2 && segments[0] == "bars" && req.HttpMethod == "GET")
                {
                    string instName  = Uri.UnescapeDataString(segments[1]);
                    string periodStr = req.QueryString["period"] ?? "Minute";
                    string countStr  = req.QueryString["count"]  ?? "50";
                    json = string.Format(
                        "{{\"instrument\":\"{0}\",\"period\":\"{1}\",\"count\":{2},\"info\":\"Utilisez une Strategy ou Indicator NinjaScript pour accéder aux barres historiques\"}}",
                        J(instName), J(periodStr), countStr);
                }

                // GET /depth/{instrument}
                else if (segments.Length == 2 && segments[0] == "depth" && req.HttpMethod == "GET")
                {
                    string instName = Uri.UnescapeDataString(segments[1]);
                    Instrument inst = Instrument.GetInstrument(instName);
                    if (inst == null)
                    { res.StatusCode = 404; json = string.Format("{{\"error\":\"Instrument '{0}' introuvable\"}}", J(instName)); }
                    else
                        json = string.Format(
                            "{{\"instrument\":\"{0}\",\"info\":\"Market depth disponible via subscription NinjaTrader\"}}",
                            J(inst.FullName));
                }

                // ── NINJASCRIPT ───────────────────────────────────────────
                // GET /indicator
                else if (path == "/indicator" && req.HttpMethod == "GET")
                {
                    string name = req.QueryString["name"] ?? "";
                    json = string.Format(
                        "{{\"indicator\":\"{0}\",\"info\":\"Valeurs accessibles depuis une Strategy ou Indicator NinjaScript actif\"}}",
                        J(name));
                }

                // GET /chart-state
                else if (path == "/chart-state" && req.HttpMethod == "GET")
                {
                    json = "{\"info\":\"chart-state accessible via NinjaScript AddOn avec référence au ChartControl actif\"}";
                }

                // POST /strategy/execute
                else if (path == "/strategy/execute" && req.HttpMethod == "POST")
                {
                    string stratName = ExtractJson(body, "strategy");
                    string instName  = ExtractJson(body, "instrument");
                    json = string.Format(
                        "{{\"info\":\"Pour exécuter '{0}' sur '{1}', lancez-la depuis NT8 Strategy Analyzer ou via ATI port 36973\"}}",
                        J(stratName), J(instName));
                }

                // ── ROUTE INCONNUE ────────────────────────────────────────
                else
                {
                    res.StatusCode = 404;
                    json = "{\"error\":\"Route inconnue\","
                         + "\"routes\":[\"/health\",\"/accounts\",\"/account/{name}\",\"/balance/{name}\","
                         + "\"/positions\",\"/position/{instrument}\","
                         + "\"/orders\",\"/order/place\",\"/order/modify\",\"/order/cancel\",\"/order/cancel-all\","
                         + "\"/quote/{instrument}\",\"/bars/{instrument}\",\"/depth/{instrument}\",\"/instruments/search\","
                         + "\"/indicator\",\"/chart-state\",\"/strategy/execute\"]}";
                }
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                json = string.Format("{{\"error\":\"{0}\"}}", J(ex.Message));
                Log(string.Format("[HTTP Server] Erreur requête : {0}", ex.Message),
                    NinjaTrader.Cbi.LogLevel.Warning);
            }

            try
            {
                byte[] buf = Encoding.UTF8.GetBytes(json);
                res.ContentLength64 = buf.Length;
                res.OutputStream.Write(buf, 0, buf.Length);
                res.OutputStream.Close();
            }
            catch { }
        }

        // ── Helpers JSON ──────────────────────────────────────────────────

        private static string ExtractJson(string json, string key)
        {
            string search = string.Format("\"{0}\"", key);
            int idx = json.IndexOf(search);
            if (idx < 0) return "";
            idx = json.IndexOf(':', idx) + 1;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return "";
            if (json[idx] == '"')
            {
                idx++;
                int end = json.IndexOf('"', idx);
                return end < 0 ? "" : json.Substring(idx, end - idx);
            }
            return "";
        }

        private static string ExtractJsonNum(string json, string key, string defaultVal)
        {
            string search = string.Format("\"{0}\"", key);
            int idx = json.IndexOf(search);
            if (idx < 0) return defaultVal;
            idx = json.IndexOf(':', idx) + 1;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return defaultVal;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
            string val = json.Substring(idx, end - idx);
            return string.IsNullOrEmpty(val) ? defaultVal : val;
        }

        private static string J(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}