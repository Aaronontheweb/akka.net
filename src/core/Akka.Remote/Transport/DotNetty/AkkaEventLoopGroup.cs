using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Dispatch;
using DotNetty.Common;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using IRunnable = DotNetty.Common.Concurrency.IRunnable;

namespace Akka.Remote.Transport.DotNetty
{
    internal sealed class AkkaEventLoopGroup : IEventLoopGroup, IEventLoop
    {
        private readonly ActorSystem _actorSystem;
        private readonly MessageDispatcher _dispatcher;
        private readonly IScheduler _scheduler;
        private readonly TaskCompletionSource<bool> _shutdownTcs;

        public AkkaEventLoopGroup(ActorSystem actorSystem) : this(
            actorSystem.Dispatchers.Lookup(RARP.For(actorSystem).Provider.RemoteSettings.Dispatcher), actorSystem)
        {
        }

        /// <summary>
        ///     For testing purposes
        /// </summary>
        /// <param name="dispatcher"></param>
        /// <param name="actorSystem"></param>
        public AkkaEventLoopGroup(MessageDispatcher dispatcher, ActorSystem actorSystem)
        {
            _dispatcher = dispatcher;
            _actorSystem = actorSystem;
            _scheduler = actorSystem.Scheduler;
            _shutdownTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Execute(IRunnable task)
        {
            InternalExecute(new AdaptedRunnable(task));
        }

        public void Execute(Action<object> action, object state)
        {
            InternalExecute(new ActionWithStateRunnable(action, state));
        }

        public void Execute(Action action)
        {
            InternalExecute(new ActionRunnable(action));
        }

        public void Execute(Action<object, object> action, object context, object state)
        {
            InternalExecute(new ActionWithContextRunnable(context, state, action));
        }

        public Task<T> SubmitAsync<T>(Func<T> func)
        {
            return SubmitAsync(func, CancellationToken.None);
        }

        public Task<T> SubmitAsync<T>(Func<T> func, CancellationToken cancellationToken)
        {
            var node = new FuncSubmitQueueNode<T>(func, cancellationToken);
            InternalExecute(node);
            return node.Completion;
        }

        public Task<T> SubmitAsync<T>(Func<object, T> func, object state)
        {
            return SubmitAsync(func, state, CancellationToken.None);
        }

        public Task<T> SubmitAsync<T>(Func<object, T> func, object state, CancellationToken cancellationToken)
        {
            var node = new StateFuncSubmitQueueNode<T>(func, state, cancellationToken);
            InternalExecute(node);
            return node.Completion;
        }

        public Task<T> SubmitAsync<T>(Func<object, object, T> func, object context, object state)
        {
            return SubmitAsync(func, context, state, CancellationToken.None);
        }

        public Task<T> SubmitAsync<T>(Func<object, object, T> func, object context, object state,
            CancellationToken cancellationToken)
        {
            var node = new StateFuncWithContextSubmitQueueNode<T>(func, context, state, cancellationToken);
            InternalExecute(node);
            return node.Completion;
        }

        public bool IsShutdown { get; private set; }
        public bool IsTerminated { get; private set; }

        public IScheduledTask Schedule(IRunnable action, TimeSpan delay)
        {
            var cts = new Cancelable(_scheduler);
            var t = new ScheduledTask(new AdaptedRunnable(action), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t;
        }

        public IScheduledTask Schedule(Action action, TimeSpan delay)
        {
            var cts = new Cancelable(_scheduler);
            var t = new ScheduledTask(new ActionRunnable(action), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t;
        }

        public IScheduledTask Schedule(Action<object> action, object state, TimeSpan delay)
        {
            var cts = new Cancelable(_scheduler);
            var t = new ScheduledTask(new ActionWithStateRunnable(action, state), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t;
        }

        public IScheduledTask Schedule(Action<object, object> action, object context, object state, TimeSpan delay)
        {
            var cts = new Cancelable(_scheduler);
            var t = new ScheduledTask(new ActionWithContextRunnable(context, state, action), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t;
        }

        public Task ScheduleAsync(Action<object> action, object state, TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var cts = new Cancelable(_scheduler.Advanced, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            var t = new ScheduledTask(new ActionWithStateRunnable(action, state), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t.Completion;
        }

        public Task ScheduleAsync(Action<object> action, object state, TimeSpan delay)
        {
            return Schedule(action, state, delay).Completion;
        }

        public Task ScheduleAsync(Action action, TimeSpan delay, CancellationToken cancellationToken)
        {
            var cts = new Cancelable(_scheduler.Advanced, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            var t = new ScheduledTask(new ActionRunnable(action), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t.Completion;
        }

        public Task ScheduleAsync(Action action, TimeSpan delay)
        {
            return Schedule(action, delay).Completion;
        }

        public Task ScheduleAsync(Action<object, object> action, object context, object state, TimeSpan delay)
        {
            return Schedule(action, context, state, delay).Completion;
        }

        public Task ScheduleAsync(Action<object, object> action, object context, object state, TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var cts = new Cancelable(_scheduler.Advanced, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            var t = new ScheduledTask(new ActionWithContextRunnable(context, state, action), cts);
            _scheduler.Advanced.ScheduleOnce(delay, t);
            return t.Completion;
        }

        public Task ShutdownGracefullyAsync()
        {
            IsShuttingDown = true;
            IsShutdown = true;
            IsTerminated = true;
            _shutdownTcs.SetResult(true);
            return _shutdownTcs.Task;
        }

        public Task ShutdownGracefullyAsync(TimeSpan quietPeriod, TimeSpan timeout)
        {
            return ShutdownGracefullyAsync();
        }

        public IEventLoop GetNext()
        {
            throw new NotImplementedException();
        }

        public Task RegisterAsync(IChannel channel)
        {
            return channel.Unsafe.RegisterAsync(this);
        }

        IEventExecutor IEventExecutorGroup.GetNext()
        {
            return GetNext();
        }

        public bool IsShuttingDown {get; private set; }
        public Task TerminationCompletion => _shutdownTcs.Task;

        public IEventLoopGroup Parent => this;

        IEventExecutorGroup IEventExecutor.Parent => this;

        public bool InEventLoop => false;

        private void InternalExecute(Dispatch.IRunnable execute)
        {
            _dispatcher.Schedule(execute);
        }

        public bool IsInEventLoop(XThread thread)
        {
            return false;
        }

        private readonly struct AdaptedRunnable : IRunnable, Dispatch.IRunnable
        {
            private readonly IRunnable _run;

            public AdaptedRunnable(IRunnable run)
            {
                _run = run;
            }

            void IRunnable.Run()
            {
                _run.Run();
            }

            void Dispatch.IRunnable.Run()
            {
                _run.Run();
            }
        }

        private sealed class ActionWithContextRunnable : Dispatch.IRunnable
        {
            private readonly Action<object, object> _actionWithState;
            private readonly object _context;
            private readonly object _state;

            public ActionWithContextRunnable(object context, object state, Action<object, object> actionWithState)
            {
                _context = context;
                _state = state;
                _actionWithState = actionWithState;
            }

            public void Run()
            {
                _actionWithState(_context, _state);
            }
        }

        abstract class FuncQueueNodeBase<T> : Dispatch.IRunnable
        {
            private readonly CancellationToken cancellationToken;
            private readonly TaskCompletionSource<T> promise;

            protected FuncQueueNodeBase(TaskCompletionSource<T> promise, CancellationToken cancellationToken)
            {
                this.promise = promise;
                this.cancellationToken = cancellationToken;
            }

            public Task<T> Completion => promise.Task;

            public void Run()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    promise.TrySetCanceled();
                    return;
                }

                try
                {
                    var result = Call();
                    promise.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // todo: handle fatal
                    promise.TrySetException(ex);
                }
            }

            protected abstract T Call();
        }

        private sealed class FuncSubmitQueueNode<T> : FuncQueueNodeBase<T>
        {
            private readonly Func<T> func;

            public FuncSubmitQueueNode(Func<T> func, CancellationToken cancellationToken)
                : base(new TaskCompletionSource<T>(), cancellationToken)
            {
                this.func = func;
            }

            protected override T Call()
            {
                return func();
            }
        }

        private sealed class StateFuncSubmitQueueNode<T> : FuncQueueNodeBase<T>
        {
            private readonly Func<object, T> func;

            public StateFuncSubmitQueueNode(Func<object, T> func, object state, CancellationToken cancellationToken)
                : base(new TaskCompletionSource<T>(state), cancellationToken)
            {
                this.func = func;
            }

            protected override T Call()
            {
                return func(Completion.AsyncState);
            }
        }

        private sealed class StateFuncWithContextSubmitQueueNode<T> : FuncQueueNodeBase<T>
        {
            private readonly object context;
            private readonly Func<object, object, T> func;

            public StateFuncWithContextSubmitQueueNode(
                Func<object, object, T> func,
                object context,
                object state,
                CancellationToken cancellationToken)
                : base(new TaskCompletionSource<T>(state), cancellationToken)
            {
                this.func = func;
                this.context = context;
            }

            protected override T Call()
            {
                return func(context, Completion.AsyncState);
            }
        }

        private sealed class ScheduledTask : IScheduledRunnable, Dispatch.IRunnable
        {
            private readonly ICancelable _cts;
            private readonly TaskCompletionSource<bool> _tcs;
            private readonly Akka.Dispatch.IRunnable _runnable;

            public ScheduledTask(Akka.Dispatch.IRunnable runnable, ICancelable cts)
            {
                _runnable = runnable;
                _cts = cts;
                _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void Run()
            {
                try
                {
                    _runnable.Run();
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }

            public bool Cancel()
            {
                _cts.Cancel();
                return _tcs.TrySetCanceled();
            }

            public TaskAwaiter GetAwaiter()
            {
                return Completion.GetAwaiter();
            }

            public PreciseTimeSpan Deadline => PreciseTimeSpan.Zero;
            public Task Completion => _tcs.Task;

            public int CompareTo(IScheduledRunnable other)
            {
                return Deadline.CompareTo(other.Deadline);
            }
        }
    }
}