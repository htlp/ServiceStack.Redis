﻿using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using ServiceStack.Common;
using ServiceStack.Logging;
using ServiceStack.Logging.Support.Logging;
using ServiceStack.Redis.Messaging;
using ServiceStack.Text;

namespace ServiceStack.Redis.Tests
{
    [Explicit("Simulating error conditions")]
    [TestFixture]
    public class RedisFailoverTests
    {
        [Test]
        public void Can_recover_from_server_terminated_client_connection()
        {
            const int SleepHoldingClientMs = 5;
            const int SleepAfterReleasingClientMs = 0;
            const int loop = 1000;

            var admin = new RedisClient("localhost");
            admin.SetConfig("timeout", "0");
            var timeout = admin.GetConfig("timeout");
            timeout.Print("timeout: {0}");

            int remaining = loop;
            var stopwatch = Stopwatch.StartNew();

            var clientManager = new PooledRedisClientManager(new[] { "localhost" })
                {
                    
                };
            loop.Times(i =>
                {
                    ThreadPool.QueueUserWorkItem(x =>
                    {
                        try
                        {
                            using (RedisClient client = (RedisClient)clientManager.GetClient())
                            {
                                client.IncrementValue("key");
                                var val = client.Get<long>("key");
                                "#{0}, isConnected: {1}".Print(val, true); //client.IsSocketConnected()
                                Thread.Sleep(SleepHoldingClientMs);
                            }
                            Thread.Sleep(SleepAfterReleasingClientMs);
                        }
                        catch (Exception ex)
                        {
                            ex.Message.Print();
                        }
                        finally
                        {
                            remaining--;
                        }
                    });
                });

            while (remaining > 0)
            {
                Thread.Sleep(10);
            }
            "Elapsed time: {0}ms".Print(stopwatch.ElapsedMilliseconds);

            var managerStats = clientManager.GetStats();
            managerStats.PrintDump();
        }

        public class Incr
        {
            public int Value { get; set; }
        }


        [Test]
        public void Can_MqServer_recover_from_server_terminated_client_connections()
        {
            LogManager.LogFactory = new ConsoleLogFactory();

            var clientManager = new PooledRedisClientManager(new[] { "localhost" })
                {
                    
                };
            var mqHost = new RedisMqServer(clientManager, retryCount: 2);

            var sum = 0;
            mqHost.RegisterHandler<Incr>(c =>
                {
                    var dto = c.GetBody();
                    sum += dto.Value;
                    "Received {0}, sum: {1}".Print(dto.Value, sum); 
                    return null;
                });

            mqHost.Start();

            10.Times(i =>
                {
                    ThreadPool.QueueUserWorkItem(x => { 
                        using (var client = mqHost.CreateMessageQueueClient())
                        {
                            "Publish: {0}...".Print(i);
                            client.Publish(new Incr { Value = i });
                            
                            Thread.Sleep(10);
                        }
                    });
            });

            ThreadPool.QueueUserWorkItem(_ =>
                {
                    using (var client = (RedisClient)clientManager.GetClient())
                    {
                        client.SetConfig("timeout", "1");
                        var clientAddrs = client.GetClientList().ConvertAll(x => x["addr"]);
                        "Killing clients: {0}...".Print(clientAddrs.Dump());
                        try
                        {
                            clientAddrs.ForEach(client.ClientKill);
                        }
                        catch (Exception ex)
                        {
                            "Client exception: {0}".Print(ex.Message);
                        }
                    }
                });

            20.Times(i =>
            {
                using (var client = mqHost.CreateMessageQueueClient())
                {
                    "Publish: {0}...".Print(i);
                    client.Publish(new Incr { Value = i });
                }

                Thread.Sleep(2000);
            });

        }

        [Test]
        public void Can_failover_at_runtime()
        {
            var failoverHost = "ny-devredis01:6380";
            var localClient = new RedisClient("localhost");
            string key = "test:failover";

            localClient.Remove(key);
            var failoverClient = new RedisClient(failoverHost);
            failoverClient.Remove(key);

            var clientManager = new PooledRedisClientManager(new[] { "localhost" });

            RunInLoop(clientManager, callback:() =>
                {
                    lock (clientManager)
                        Monitor.Pulse(clientManager);
                });

            Thread.Sleep(100);

            clientManager.FailoverTo(failoverHost);

            lock (clientManager)
                Monitor.Wait(clientManager);

            var localIncr = localClient.Get<int>(key);
            var failoverIncr = failoverClient.Get<int>(key);
            Assert.That(localIncr, Is.GreaterThan(0));
            Assert.That(failoverIncr, Is.GreaterThan(0));
            Assert.That(localIncr + failoverIncr, Is.EqualTo(100));
        }


        public static bool RunInLoop(PooledRedisClientManager clientManager, int iterations = 100, int sleepMs = 10, Action callback=null)
        {
            int count = 0;
            int errors = 0;
            
            10.Times(i =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (iterations-- > 0)
                    {
                        using (var client = clientManager.GetClient())
                        {
                            try
                            {
                                var result = client.Increment("test:failover", 1);
                                if (++count % (iterations / 10) == 0)
                                    lock (clientManager)
                                        Console.WriteLine("count: {0}, errors: {1}", count, errors);
                            }
                            catch (Exception ex)
                            {
                                errors++;
                            }
                            Thread.Sleep(sleepMs);
                        }
                    }

                    if (callback != null)
                    {
                        callback();
                        callback = null;
                    }
                });
            });

            return true;
        }
    }
}