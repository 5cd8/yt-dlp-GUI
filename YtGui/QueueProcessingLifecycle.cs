namespace YtGui
{
    internal enum QueueProcessingState { Idle, Running, Stopping, Updating }

    internal enum AutoStartDecision { Start, Reserve, Ignore }

    internal static class QueueProcessingLifecycle
    {
        public static AutoStartDecision DecideAutoStart(QueueProcessingState state, bool isStartReserved)
            => state switch
            {
                QueueProcessingState.Idle => AutoStartDecision.Start,
                QueueProcessingState.Stopping or QueueProcessingState.Updating when !isStartReserved => AutoStartDecision.Reserve,
                _ => AutoStartDecision.Ignore,
            };

        // 起動時のツール更新（Updating）が終わった時点で、予約された開始があり、始める項目があれば始める。
        public static bool ShouldStartAfterUpdates(bool isStartReserved, bool hasQueuedItems)
            => isStartReserved && hasQueuedItems;

        public static bool ShouldStartNextAfterFinished(QueueProcessingState stateAtFinish, bool isStartReserved, bool hasQueuedItems, bool hasFaulted)
            => !hasFaulted
                && hasQueuedItems
                && (stateAtFinish == QueueProcessingState.Running
                    || (stateAtFinish == QueueProcessingState.Stopping && isStartReserved));
    }
}
