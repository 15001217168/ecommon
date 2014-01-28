﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ECommon.Logging;

namespace ECommon.Scheduling
{
    public class DefaultScheduleService : IScheduleService
    {
        private readonly IDictionary<int, Timer> _timerDict = new ConcurrentDictionary<int, Timer>();
        private int _taskCount;
        private readonly ILogger _logger;

        public DefaultScheduleService(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.Create(GetType().Name);
        }

        public int ScheduleTask(Action action, int dueTime, int period)
        {
            var taskId = Interlocked.Increment(ref _taskCount);
            var timer = new Timer((obj) =>
            {
                var state = (TimerState)obj;
                Timer currentTimer;
                if (_timerDict.TryGetValue(state.TaskId, out currentTimer))
                {
                    currentTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Schedule task has exception.", ex);
                    }
                    finally
                    {
                        currentTimer.Change(state.Period, state.Period);
                    }
                }
            }, new TimerState(taskId, dueTime, period), Timeout.Infinite, Timeout.Infinite);

            _timerDict.Add(taskId, timer);

            timer.Change(dueTime, period);

            return taskId;
        }
        public void ShutdownTask(int taskId)
        {
            Timer timer;
            if (_timerDict.TryGetValue(taskId, out timer))
            {
                timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        class TimerState
        {
            public int TaskId;
            public int DueTime;
            public int Period;

            public TimerState(int taskId, int dueTime, int period)
            {
                TaskId = taskId;
                DueTime = dueTime;
                Period = period;
            }
        }
    }
}
