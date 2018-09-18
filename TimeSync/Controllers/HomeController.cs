using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using Microsoft.AspNet.SignalR;

namespace TimeSync.Controllers
{
    public class TimeSyncMessage
    {
        public Guid PingId { get; set; }

        public string ConnectionId { get; set; }

        public DateTime ServerTimeSent { get; set; }


        public DateTime ClientTimeSent { get; set; }


        public DateTime ServerTimeReceived { get; set; }


        public int Roundtrip
        {
            get
            {
                if (ServerTimeReceived == default(DateTime))
                {
                    return 0;
                }
                TimeSpan span = ServerTimeReceived - ServerTimeSent;
                return (int)span.TotalMilliseconds;
            }
        }

        public DateTime CalculatedClientDateTime
        {
            get
            {
                //assume half roundtrip. Error is then +/- 1/2 roundtrip also
                return ClientTimeSent.Subtract(new TimeSpan(0, 0, 0, 0, Roundtrip / 2));
            }
        }

        public TimeSpan ClientOffsetTimespan
        {
            get
            {
                if (CalculatedClientDateTime == default(DateTime))
                {
                    return new TimeSpan();
                }
                TimeSpan span = CalculatedClientDateTime - ServerTimeSent;
                return span;
            }
        }

        public DateTime ServerCurrentTime
        {
            get
            {
                return DateTime.UtcNow;
            }
        }

        public DateTime ClientCurrentTime
        {
            get
            {
                return ServerCurrentTime.Add(ClientOffsetTimespan);
            }
        }



        public static TimeSyncMessage NewFrom(TimeSyncMessage message)
        {
            return new TimeSyncMessage
            {
                PingId = message.PingId,
                ConnectionId = message.ConnectionId,
                ServerTimeSent = message.ServerTimeSent,
                ClientTimeSent = message.ClientTimeSent,
                ServerTimeReceived = message.ServerTimeReceived

            };
        }

    }

    public class TimeSync : Hub
    {
        public static ConcurrentDictionary<Guid, TimeSyncMessage> Pings = new ConcurrentDictionary<Guid, TimeSyncMessage>();

        public static ConcurrentDictionary<string, TimeSyncMessage> Pongs = new ConcurrentDictionary<string, TimeSyncMessage>();

        public static ConcurrentDictionary<string, object> ClientIds = new ConcurrentDictionary<string, object>();

        public static readonly System.Timers.Timer _Timer = new System.Timers.Timer();

        static TimeSync()
        {
            _Timer.Interval = 5000;
            _Timer.Elapsed += TimerElapsed;
            _Timer.Start();
        }

        static void TimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            StartPing_Static();
        }

        // Overridable hub methods  
        public override Task OnConnected()
        {
            ClientIds.AddOrUpdate(Context.ConnectionId, (a) => null, (a,b) => null);
            ShowClients();
            return Task.CompletedTask;
        }
        public override Task OnReconnected()
        {

            ClientIds.AddOrUpdate(Context.ConnectionId, (a) => null, (a, b) => null);
            ShowClients();
            return Task.CompletedTask;
        }
        public override Task OnDisconnected(bool stopCalled)
        {
            object client;

            ClientIds.TryRemove(Context.ConnectionId, out client);
            ShowClients();

            return Task.CompletedTask;
        }


        public void StartPing()
        {
            StartPing_Static();
        }

        public static void StartPing_Static()
        {
            var ping = new TimeSyncMessage
            {
                PingId = Guid.NewGuid(),
                ServerTimeSent = DateTime.UtcNow

            };

            //simulate down latency here
            System.Threading.Thread.Sleep(2000);

            var hub = GlobalHost.ConnectionManager.GetHubContext<TimeSync>();

            hub.Clients.All.timeSyncRequest(ping);

            Pings.AddOrUpdate(ping.PingId, k => ping, (k, old) => ping);


        }

        public void Pong(Guid pingId, long clientTimeSent)
        {
            //simulate up latency here
            System.Threading.Thread.Sleep(2000);

            var serverTimeReceived = DateTime.UtcNow;

            var ping = Pings[pingId];

            var clientPing = TimeSyncMessage.NewFrom(ping);

            clientPing.ConnectionId = Context.ConnectionId;
            clientPing.ClientTimeSent = JavaScriptDateConverter.Convert(clientTimeSent);
            clientPing.ServerTimeReceived = serverTimeReceived;

            Pongs.AddOrUpdate(clientPing.ConnectionId, c => clientPing, (c, old) =>
            {
                if (old.Roundtrip > clientPing.Roundtrip)
                {
                    //this was more accurate...use it
                    return clientPing;
                }
                else
                {
                    return old;
                }


            });

            ShowPings();

        }

        public void ShowPings()
        {

            Clients.All.displayPings(Pongs.Values.ToList());

        }

        public void ShowClients()
        {

            Clients.All.displayClients(ClientIds.Keys.ToList());

        }

        //Usage
        public string SendCommand(long clientTimeSent, string message = "General Command")
        {
            Random r = new Random();

            var upArtificialLatency = r.Next(500, 1500);
            var downArtificialLatency = r.Next(0, 500);

            //simulate up latency here
            System.Threading.Thread.Sleep(upArtificialLatency);

            var serverReceiveDateTime = DateTime.UtcNow;
            var clientDateTime = JavaScriptDateConverter.Convert(clientTimeSent);
            



            Pongs.TryGetValue(Context.ConnectionId, out TimeSyncMessage senderTimeSync);




            var sendTimeFromServerPerspective = clientDateTime.Subtract(senderTimeSync.ClientOffsetTimespan);


            TimeSpan possibleUpLatency = serverReceiveDateTime - sendTimeFromServerPerspective;

            if(possibleUpLatency.TotalMilliseconds >= 1000)
            {
                //fail command
                return $"Command rejected: already {possibleUpLatency.TotalMilliseconds}ms old.";
            }



            ClientIds.Keys.Where(id => id != Context.ConnectionId).ToList().ForEach(id =>
            {
                //get 
                if (!Pongs.TryGetValue(Context.ConnectionId, out TimeSyncMessage receiverTimeSync))
                {
                    return;
                }


                var sendTimeFromReceiverPerspective = sendTimeFromServerPerspective.Add(receiverTimeSync.ClientOffsetTimespan);


                var wrappedMessage = new
                {
                    sendTimeFromSendersPerspective = clientDateTime,
                    sendTimeFromServerPerspective,
                    sendTimeFromReceiverPerspective,
                    sendTime = JavaScriptDateConverter.Convert(sendTimeFromReceiverPerspective),
                    message,
                    upArtificialLatency,
                    downArtificialLatency,
                    totalArtificialLatency = upArtificialLatency + downArtificialLatency
                };


                //simulate down latency here
                System.Threading.Thread.Sleep(downArtificialLatency);

                Clients.Client(id).receiveCommand(wrappedMessage);


            });

            return "";
        }

    }
     
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            return View();
        }
    }



    // from: https://gist.github.com/marlun78/1347789/09e424ef53d2f164c5479384791721d1c493c77f
    public static class JavaScriptDateConverter
    {
        // 
        private static DateTime _jan1st1970 = new DateTime(1970, 1, 1);

        /// <summary>
        /// Converts a DateTime into a (JavaScript parsable) Int64.
        /// </summary>
        /// <param name="from">The DateTime to convert from</param>
        /// <returns>An integer value representing the number of milliseconds since 1 January 1970 00:00:00 UTC.</returns>
        public static long Convert(DateTime from)
        {
            return System.Convert.ToInt64((from - _jan1st1970).TotalMilliseconds);
        }

        /// <summary>
        /// Converts a (JavaScript parsable) Int64 into a DateTime.
        /// </summary>
        /// <param name="from">An integer value representing the number of milliseconds since 1 January 1970 00:00:00 UTC.</param>
        /// <returns>The date as a DateTime</returns>
        public static DateTime Convert(long from)
        {
            return _jan1st1970.AddMilliseconds(from);
        }
    }
     
}
 
