namespace YtGui
{
    internal enum QueueProcessingState { Idle, Running, Stopping }

    internal enum AutoStartDecision { Start, Reserve, Ignore }

    internal static class QueueProcessingLifecycle
    {
        public static AutoStartDecision DecideAutoStart(QueueProcessingState state, bool isStartReserved)
            => state switch
            {
                QueueProcessingState.Idle => AutoStartDecision.Start,
                QueueProcessingState.Stopping when !isStartReserved => AutoStartDecision.Reserve,
                _ => AutoStartDecision.Ignore,
            };

        public static bool ShouldStartNextAfterFinished(QueueProcessingState stateAtFinish, bool isStartReserved, bool hasQueuedItems, bool hasFaulted)
            => !hasFaulted
                && hasQueuedItems
                && (stateAtFinish == QueueProcessingState.Running
                    || (stateAtFinish == QueueProcessingState.Stopping && isStartReserved));
    }
}
