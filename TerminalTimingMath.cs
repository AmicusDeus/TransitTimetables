namespace TransitTimetables
{
    // Pure phase/offset calculations for multiple timing points on one timetable grid.
    // The stored slot minute always belongs to Terminal A's schedule; a secondary timing
    // point only views that same slot through its cumulative route offset.
    public static class TerminalTimingMath
    {
        public static int ScheduleReferenceMinute(int clockMinute, int timingPointOffset)
            => clockMinute - timingPointOffset;

        public static int TimingPointDepartureMinute(int primarySlotMinute, int timingPointOffset)
            => primarySlotMinute + timingPointOffset;

        public static int MinutesUntilTimingPoint(int primarySlotMinute, int timingPointOffset, int clockMinute)
            => TimingPointDepartureMinute(primarySlotMinute, timingPointOffset) - clockMinute;

        // ScheduleMath expresses departures relative to the current clock day's midnight, so one physical slot may be
        // 1430 before midnight and -10 after midnight. Add the runtime day epoch before comparing ownership; otherwise
        // Terminal A and Terminal B can claim the same trip under two different integer representations.
        public static long CanonicalScheduleMinute(int scheduleMinute, int scheduleDay)
            => (long)scheduleDay * 1440L + scheduleMinute;

        public static bool CandidateWithinAssignmentWindow(int minutesUntil, int maxInterval, int stepsTaken)
            => minutesUntil >= 0 && minutesUntil <= maxInterval * (stepsTaken + 1);

        public static int OffsetFromSlotOrigin(int stopOffset, int slotOriginOffset)
            => stopOffset - slotOriginOffset;

        public static bool SecondaryShouldAcquireSlot(bool hasSlot)
            => !hasSlot;

        public static int TimingPointHoldBound(int maxInterval, int ownedWait, int maxSlotSteps,
            int slackMinutes, int layoverMinutes)
        {
            int bound = maxInterval;
            if (ownedWait > bound)
                bound = System.Math.Min(ownedWait, maxInterval * (maxSlotSteps + 1));
            return bound + slackMinutes + layoverMinutes;
        }

        // Pure form of HoldStop's departure decision. A vehicle with a slot waits only when it arrived early;
        // a slotless, on-time, or late vehicle uses its arrival as the release target.
        public static uint DepartureTarget(uint arrivedFrame, uint scheduledFrame, bool hasSlot)
            => hasSlot && arrivedFrame < scheduledFrame ? scheduledFrame : arrivedFrame;

        public static bool ShouldHold(uint currentFrame, uint targetFrame, bool overrun)
            => targetFrame > currentFrame && !overrun;
    }
}
