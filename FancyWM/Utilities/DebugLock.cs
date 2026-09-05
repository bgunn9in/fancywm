using System;
using System.Diagnostics;

using SystemLock = System.Threading.Lock;

namespace FancyWM.Utilities
{
    internal class DebugLock
    {
        private static readonly TimeSpan s_minSteadyTime = TimeSpan.FromSeconds(10);
        private static readonly Stopwatch s_sw = new();

        public TimeSpan MaxExpectedLockTime { get; private init; }
        public TimeSpan MaxLockTime { get; private set; }
        public TimeSpan LastLockTime { get; private set; }

        internal bool IsHeldByCurrentThread => m_lock.IsHeldByCurrentThread;

        public ref struct Scope
        {
            internal DebugLock? LockObj;
            internal SystemLock.Scope LockScope;
            internal TimeSpan LockTime;

            public Scope(DebugLock lockObj, SystemLock.Scope scope)
            {
                LockObj = lockObj;
                LockScope = scope;
            }

            public void Dispose()
            {
                DebugLock? lockObj = LockObj;
                if (lockObj is not null)
                {
                    LockObj = null;
                    lockObj.Exit(this);
                }
            }
        }

        private readonly SystemLock m_lock = new();

        static DebugLock()
        {
            s_sw.Start();
        }

        public DebugLock(TimeSpan maxExpectedDuration)
        {
            MaxExpectedLockTime = maxExpectedDuration;
        }

        internal Scope EnterScope()
        {
            var s = new Scope(this, m_lock.EnterScope())
            {
                LockTime = s_sw.Elapsed
            };
            return s;
        }

        private void Exit(Scope scope)
        {
            var elapsed = s_sw.Elapsed - scope.LockTime;
            LastLockTime = elapsed;
            if (elapsed > MaxLockTime)
            {
                MaxLockTime = elapsed;
            }
            scope.LockScope.Dispose();
            if (elapsed > MaxExpectedLockTime && scope.LockTime >= s_minSteadyTime)
            {
                var frame = new StackTrace().GetFrame(2);
                App.Current.Logger.Information($"Critical section exited after {elapsed.TotalMilliseconds}ms at {frame} above threshold of {MaxExpectedLockTime.TotalMilliseconds}ms");
            }
        }
    }
}
