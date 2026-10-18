using System;
using System.Collections.Generic;

namespace Cldou.Core.Bus
{
    /// <summary>总线事件信封。</summary>
    public sealed class BusEvent
    {
        public string Topic { get; private set; }
        public object Payload { get; private set; }
        public object Source { get; private set; }

        public BusEvent(string topic, object payload, object source)
        {
            Topic = topic;
            Payload = payload;
            Source = source;
        }
    }

    /// <summary>
    /// 进程内、主线程泵送的事件总线。
    ///
    /// 特性：
    ///   - 发布/订阅按主题隔离；优先级大者先收到；同优先级按订阅顺序。
    ///   - 发布只入队，Pump() 在游戏主线程内统一分发（保证 mod 回调均在主线程）。
    ///   - 单个订阅者抛异常不影响其它订阅者（通过 DispatchError 上报）。
    ///   - 支持按 owner 批量退订（mod 禁用时一键清理）。
    /// </summary>
    public sealed class EventBus
    {
        private sealed class Subscription
        {
            public long Id;
            public string Topic;
            public Action<BusEvent> Handler;
            public int Priority;
            public object Owner;
            public bool Disposed;
        }

        private sealed class SubscriptionToken : IDisposable
        {
            private EventBus _bus;
            private Subscription _subscription;

            public SubscriptionToken(EventBus bus, Subscription subscription)
            {
                _bus = bus;
                _subscription = subscription;
            }

            public void Dispose()
            {
                EventBus bus = _bus;
                if (bus != null)
                {
                    _bus = null;
                    bus.RemoveSubscription(_subscription);
                }
            }
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, List<Subscription>> _subscriptions =
            new Dictionary<string, List<Subscription>>(StringComparer.Ordinal);
        private readonly Queue<BusEvent> _queue = new Queue<BusEvent>();
        private long _nextId = 1;

        /// <summary>订阅者抛异常时触发（异常, 主题）。仅用于日志，不允许再次抛出。</summary>
        public event Action<Exception, string> DispatchError;

        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _queue.Count;
                }
            }
        }

        public IDisposable Subscribe(string topic, Action<BusEvent> handler, int priority = 0, object owner = null)
        {
            if (topic == null) throw new ArgumentNullException("topic");
            if (handler == null) throw new ArgumentNullException("handler");

            var subscription = new Subscription
            {
                Id = _nextId++,
                Topic = topic,
                Handler = handler,
                Priority = priority,
                Owner = owner
            };

            lock (_lock)
            {
                List<Subscription> list;
                if (!_subscriptions.TryGetValue(topic, out list))
                {
                    list = new List<Subscription>();
                    _subscriptions[topic] = list;
                }
                list.Add(subscription);
            }

            return new SubscriptionToken(this, subscription);
        }

        private void RemoveSubscription(Subscription subscription)
        {
            if (subscription == null)
            {
                return;
            }

            lock (_lock)
            {
                if (subscription.Disposed)
                {
                    return;
                }
                subscription.Disposed = true;

                List<Subscription> list;
                if (_subscriptions.TryGetValue(subscription.Topic, out list))
                {
                    list.Remove(subscription);
                    if (list.Count == 0)
                    {
                        _subscriptions.Remove(subscription.Topic);
                    }
                }
            }
        }

        /// <summary>退订某个 owner 的全部订阅，返回退订数量。</summary>
        public int UnsubscribeOwner(object owner)
        {
            if (owner == null)
            {
                return 0;
            }

            int removed = 0;
            lock (_lock)
            {
                var emptyTopics = new List<string>();
                foreach (KeyValuePair<string, List<Subscription>> pair in _subscriptions)
                {
                    removed += pair.Value.RemoveAll(s =>
                    {
                        if (ReferenceEquals(s.Owner, owner))
                        {
                            s.Disposed = true;
                            return true;
                        }
                        return false;
                    });

                    if (pair.Value.Count == 0)
                    {
                        emptyTopics.Add(pair.Key);
                    }
                }

                foreach (string topic in emptyTopics)
                {
                    _subscriptions.Remove(topic);
                }
            }
            return removed;
        }

        /// <summary>发布事件（入队，不立即分发）。</summary>
        public void Publish(string topic, object payload = null, object source = null)
        {
            if (topic == null) throw new ArgumentNullException("topic");

            lock (_lock)
            {
                _queue.Enqueue(new BusEvent(topic, payload, source));
            }
        }

        /// <summary>在主线程分发队列中的事件，最多 maxEvents 条。返回处理数量。</summary>
        public int Pump(int maxEvents = 256)
        {
            int processed = 0;
            while (processed < maxEvents)
            {
                BusEvent busEvent;
                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        break;
                    }
                    busEvent = _queue.Dequeue();
                }

                Dispatch(busEvent);
                processed++;
            }
            return processed;
        }

        private void Dispatch(BusEvent busEvent)
        {
            List<Subscription> snapshot;
            lock (_lock)
            {
                List<Subscription> list;
                if (!_subscriptions.TryGetValue(busEvent.Topic, out list) || list.Count == 0)
                {
                    return;
                }
                snapshot = new List<Subscription>(list);
            }

            snapshot.Sort((a, b) =>
            {
                int cmp = b.Priority.CompareTo(a.Priority);
                if (cmp != 0) return cmp;
                return a.Id.CompareTo(b.Id);
            });

            foreach (Subscription subscription in snapshot)
            {
                bool disposed;
                lock (_lock)
                {
                    disposed = subscription.Disposed;
                }
                if (disposed)
                {
                    continue;
                }

                try
                {
                    subscription.Handler(busEvent);
                }
                catch (Exception ex)
                {
                    Action<Exception, string> handler = DispatchError;
                    if (handler != null)
                    {
                        try
                        {
                            handler(ex, busEvent.Topic);
                        }
                        catch
                        {
                            // 诊断回调自身异常必须吞掉，避免破坏分发
                        }
                    }
                }
            }
        }

        /// <summary>清空队列与全部订阅（关闭时使用）。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _queue.Clear();
                _subscriptions.Clear();
            }
        }
    }
}
