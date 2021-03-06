﻿using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using MessagePack;
using NettyModel.Event;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace DotNettyClient.DotNetty
{
    public class EchoClientHandler : SimpleChannelInboundHandler<Object>
    {
        public event Action ReconnectServer;
        public event Action<TestEvent> ReceiveEventFromClientEvent;
        public event Action<string> RecordLogEvent;
        private IChannelHandlerContext channelHandlerContext;
        private List<TestEventCount> lstNeedSendDatas = new List<TestEventCount>();   // 用于存放需要发送的数据
        private static object lockOjb = new object();                       // 读取数据锁
        private List<TestEvent> lstSendPings = new List<TestEvent>();       // 用于存放发送的ping包
        private const int MAX_NO_RESPONSE_PING_COUNT = 2;                   // 未收到ping回应的数据包最大个数
        private bool isConnect;
        private int total = 0;

        /// <summary>
        /// 发送数据到服务端
        /// </summary>
        /// <param name="testEvent"></param>
        public void SendData(TestEvent testEvent)
        {
            try
            {
                lock (lockOjb)
                {
                    lstNeedSendDatas.Add(new TestEventCount { TestEvent = testEvent });
                }
                if (channelHandlerContext == null)
                {
                    RecordLogEvent?.Invoke($"未连接服务，无法发送数据");
                    return;
                }
                SendData();
            }
            catch (Exception ex)
            {
                RecordLogEvent?.Invoke($"发送数据异常：{ex.Message}");
            }
        }

        private bool isRunning = false;
        /// <summary>
        /// 发送数据
        /// </summary>
        private void SendData()
        {
            if (isRunning)
            {
                return;
            }
            isRunning = true;
            ThreadPool.QueueUserWorkItem(sen =>
            {
                while (true)
                {
                    try
                    {
                        TestEventCount sendEvent = null;
                        lock (lockOjb)
                        {
                            lstNeedSendDatas.RemoveAll(cu => cu.TryCount >= TestEventCount.MAX_TRY_COUNT);
                            sendEvent = lstNeedSendDatas.FirstOrDefault();
                        }
                        if (sendEvent != null)
                        {
                            channelHandlerContext.WriteAndFlushAsync(sendEvent.TestEvent);
                            RecordLogEvent?.Invoke($"发送到服务端：{JsonConvert.SerializeObject(sendEvent.TestEvent)}");
                            sendEvent.TryCount++;
                        }
                        // 没有数据包，发送数据包
                        else
                        {
                            SendPing();
                        }
                    }
                    catch (Exception ex2)
                    {
                        RecordLogEvent?.Invoke($"发送到服务端异常：{ex2.Message}");
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            });
        }

        public override bool IsSharable => true;//标注一个channel handler可以被多个channel安全地共享。

        /// <summary>
        /// 收到服务端回应消息
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="msg"></param>
        protected override void ChannelRead0(IChannelHandlerContext ctx, object msg)
        {
            try
            {
                TestEvent testEvent = MessagePackSerializer.Deserialize<TestEvent>(MessagePackSerializer.Serialize(msg));
                if (testEvent.code == EventCode.Ping)
                {
                    lstSendPings.Clear();
                    RecordLogEvent?.Invoke("收到Android端心跳回应");
                    return;
                }
                else if (testEvent.code == EventCode.OK)
                {
                    lock (lockOjb)
                    {
                        lstNeedSendDatas.RemoveAll(cu => cu.TestEvent.reqId == testEvent.reqId);
                    }
                }
                else if (testEvent.code == EventCode.Chat)
                {
                    ReceiveEventFromClientEvent?.Invoke(testEvent);
                }
                var eventMsg = JsonConvert.SerializeObject(testEvent);
                RecordLogEvent?.Invoke($"收到Android端消息：{eventMsg}");
            }
            catch (Exception ex)
            {
                RecordLogEvent?.Invoke($"读取数据异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 读写超时通知
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="evt"></param>
        public override void UserEventTriggered(IChannelHandlerContext ctx, object evt)
        {
            if (!(evt is IdleStateEvent))
            {
                return;
            }

            SendPing();
        }

        private void SendPing()
        {
            // 发送的ping，超过一定范围，认为与服务端断开连接，需要重连
            if (lstSendPings.Count >= MAX_NO_RESPONSE_PING_COUNT)
            {
                channelHandlerContext.CloseAsync();
                RecordLogEvent?.Invoke($"{lstSendPings.Count} 次未收到心跳回应，重连服务器");
                lstSendPings.Clear();
                Reconnect(channelHandlerContext);
                return;
            }

            // 向服务端发送心跳
            var testEvent = new TestEvent()
            {
                code = EventCode.Ping,
                reqId = Guid.NewGuid().ToString()
            };
            lstSendPings.Add(testEvent);
            channelHandlerContext.WriteAndFlushAsync(testEvent);
            RecordLogEvent?.Invoke("发送心跳");
        }


        public override void HandlerAdded(IChannelHandlerContext context)
        {
            this.channelHandlerContext = context;
            RecordLogEvent?.Invoke($"HandlerAdded.");
            base.HandlerAdded(context);
        }

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            RecordLogEvent?.Invoke($"HandlerRemoved");
            base.HandlerRemoved(context);
        }

        public override void ChannelReadComplete(IChannelHandlerContext context)
        {
            base.ChannelReadComplete(context);
            context.Flush();
            RecordLogEvent?.Invoke("ChannelReadComplete:" + context);
        }

        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            base.ChannelRegistered(context);
            RecordLogEvent?.Invoke("Client ChannelRegistered:" + context);
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            total = 0;
            RecordLogEvent?.Invoke("Client channelActive:" + context);
            isConnect = true;
        }

        public override void ChannelInactive(IChannelHandlerContext context)
        {
            base.ChannelInactive(context);
            RecordLogEvent?.Invoke("Client ChannelInactive:" + context);
            isConnect = false;
        }

        public override void ChannelUnregistered(IChannelHandlerContext context)
        {
            base.ChannelUnregistered(context);
            Reconnect(context);
            RecordLogEvent?.Invoke("Client ChannelUnregistered:" + context);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            RecordLogEvent?.Invoke("Exception: " + exception);
            context.CloseAsync();
        }

        public void Disconnect()
        {
            if (channelHandlerContext != null)
                channelHandlerContext.DisconnectAsync();
        }

        public void Reconnect()
        {
            if (channelHandlerContext != null)
                channelHandlerContext.ConnectAsync(channelHandlerContext.Channel.RemoteAddress, channelHandlerContext.Channel.LocalAddress);
        }

        public void Reconnect(IChannelHandlerContext context)
        {
            Interlocked.Increment(ref total);
            if (total == 1)
            {
                ReconnectServer?.Invoke();

            }
        }
    }
}
