using System;
using System.Collections.Generic;
using TransitTimetables;
using Unity.Entities;

internal static class TerminalTimingMathTests
{
    private static readonly Entity TerminalA = new Entity(101);
    private static readonly Entity TerminalB = new Entity(202);
    private static readonly Entity OtherStop = new Entity(303);

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("single-terminal configuration remains backward-compatible", SingleTerminalConfigurationRemainsBackwardCompatible),
            ("Terminal B absent uses Entity.Null", TerminalBAbsentUsesEntityNull),
            ("Terminal B configured", TerminalBConfigured),
            ("same terminal is rejected", SameTerminalIsRejected),
            ("terminal recognition helper", TerminalRecognitionHelper),
            ("loop line works with only Terminal A", LoopLineWorksWithOnlyTerminalA),
            ("timetable behavior around midnight", TimetableBehaviorAroundMidnight),
            ("slot ownership remains canonical across midnight", SlotOwnershipRemainsCanonicalAcrossMidnight),
            ("peak off-peak and night transitions", PeakOffPeakAndNightTransitions),
            ("custom peak transition", CustomPeakTransition),
            ("early vehicle at Terminal A", EarlyVehicleAtTerminalA),
            ("early vehicle at Terminal B", EarlyVehicleAtTerminalB),
            ("Terminal B layover is not clamped", TerminalBLayoverIsNotClamped),
            ("late vehicle is not unnecessarily held", LateVehicleIsNotHeld),
            ("timetable slot progression", TimetableSlotProgression),
            ("no duplicate terminal semantics", NoDuplicateTerminalSemantics),
            ("default schedule state", DefaultScheduleState),
            ("schedule serialization order remains compatible", ScheduleSerializationOrderRemainsCompatible),
            ("legacy schedule payload deserializes", LegacySchedulePayloadDeserializes),
            ("schedule serialization round trip", ScheduleSerializationRoundTrip),
            ("Terminal B serialization is separate and versioned", TerminalBSerializationIsSeparateAndVersioned),
            ("Terminal B version zero safely defaults to null", TerminalBVersionZeroDefaultsToNull),
        };

        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Test failed: {name}", exception);
            }
        }

        return 0;
    }

    private static void SingleTerminalConfigurationRemainsBackwardCompatible()
    {
        TimetableSchedule schedule = Schedule(first: 360, peak: 5, offPeak: 10, night: 20, terminal: TerminalA);
        LineTerminalB second = LineTerminalB.Default();

        Equal(TerminalA, LineTerminalSelection.PrimaryTerminal(schedule), "Terminal A remains the existing field");
        False(LineTerminalSelection.HasSecondTerminal(second), "single-terminal schedule has no Terminal B");
        True(LineTerminalSelection.IsConfiguredTerminal(schedule, second, TerminalA), "Terminal A remains a timing point");
        Equal(410, ScheduleMath.NextDeparture(Settings(), schedule, default, LineSchedule.DayAndNight, 410), "legacy departure grid");
    }

    private static void TerminalBAbsentUsesEntityNull()
    {
        LineTerminalB second = LineTerminalB.Default();

        Equal(Entity.Null, second.m_Stop, "Terminal B default");
        Equal(Entity.Null, LineTerminalSelection.SecondaryTerminal(second), "secondary helper default");
        False(LineTerminalSelection.HasSecondTerminal(second), "no second terminal");
        False(LineTerminalSelection.IsSecondaryTerminal(second, Entity.Null), "Entity.Null is not a terminal");
    }

    private static void TerminalBConfigured()
    {
        LineTerminalB second = new LineTerminalB { m_Stop = TerminalB };

        True(LineTerminalSelection.HasSecondTerminal(second), "Terminal B is present");
        Equal(TerminalB, LineTerminalSelection.SecondaryTerminal(second), "secondary helper");
        True(LineTerminalSelection.IsSecondaryTerminal(second, TerminalB), "Terminal B is recognized");
    }

    private static void SameTerminalIsRejected()
    {
        False(LineTerminalSelection.CanConfigureSecondary(TerminalA, TerminalA), "A cannot also be B");
        False(LineTerminalSelection.CanConfigureSecondary(TerminalA, Entity.Null), "null cannot be configured as B");
        True(LineTerminalSelection.CanConfigureSecondary(TerminalA, TerminalB), "distinct B is accepted");
    }

    private static void TerminalRecognitionHelper()
    {
        TimetableSchedule schedule = Schedule(terminal: TerminalA);
        LineTerminalB second = new LineTerminalB { m_Stop = TerminalB };

        True(LineTerminalSelection.IsPrimaryTerminal(schedule, TerminalA), "A recognition");
        False(LineTerminalSelection.IsPrimaryTerminal(schedule, TerminalB), "B is not A");
        True(LineTerminalSelection.IsConfiguredTerminal(schedule, second, TerminalA), "configured A");
        True(LineTerminalSelection.IsConfiguredTerminal(schedule, second, TerminalB), "configured B");
        False(LineTerminalSelection.IsConfiguredTerminal(schedule, second, OtherStop), "intermediate stop");
        False(LineTerminalSelection.IsConfiguredTerminal(schedule, second, Entity.Null), "null stop");
    }

    private static void LoopLineWorksWithOnlyTerminalA()
    {
        TimetableSchedule schedule = Schedule(first: 300, peak: 8, offPeak: 12, night: 30, terminal: TerminalA);
        LineTerminalB second = LineTerminalB.Default();

        True(LineTerminalSelection.IsConfiguredTerminal(schedule, second, TerminalA), "loop timing point");
        False(LineTerminalSelection.HasSecondTerminal(second), "loop does not require B");

        int[] departures = new int[3];
        Equal(3, ScheduleMath.Upcoming(Settings(), schedule, default, LineSchedule.DayAndNight, 300, departures, 3), "loop departure count");
        Sequence(new[] { 300, 330, 360 }, departures, "loop departure progression");
    }

    private static void TimetableBehaviorAroundMidnight()
    {
        TimetableSchedule schedule = Schedule(first: 0, peak: 10, offPeak: 10, night: 10, terminal: TerminalA);
        TransitTimetablesSetting settings = Settings();

        Equal(1440, ScheduleMath.NextDeparture(settings, schedule, default, LineSchedule.DayAndNight, 1435), "next after 23:55");
        Equal(0, ScheduleMath.PreviousDeparture(settings, schedule, default, LineSchedule.DayAndNight, 5), "previous after midnight");
        Equal(-50, TerminalTimingMath.ScheduleReferenceMinute(10, 60), "Terminal B reference crosses midnight");
        Equal(10, TerminalTimingMath.MinutesUntilTimingPoint(1410, 30, 1430), "B wait across midnight");
    }

    private static void SlotOwnershipRemainsCanonicalAcrossMidnight()
    {
        long aBeforeMidnight = TerminalTimingMath.CanonicalScheduleMinute(1430, 0);
        long bAfterMidnight = TerminalTimingMath.CanonicalScheduleMinute(-10, 1);
        long nextMidnightBeforeWrap = TerminalTimingMath.CanonicalScheduleMinute(1440, 0);
        long nextMidnightAfterWrap = TerminalTimingMath.CanonicalScheduleMinute(0, 1);

        Equal(aBeforeMidnight, bAfterMidnight, "23:50 A slot and 00:20 B phase share one identity");
        Equal(nextMidnightBeforeWrap, nextMidnightAfterWrap, "midnight slot identity");

        var claims = new HashSet<long> { aBeforeMidnight };
        False(claims.Add(bAfterMidnight), "same physical departure cannot be claimed twice");
        True(claims.Add(TerminalTimingMath.CanonicalScheduleMinute(1440, 1)), "next day's distinct slot remains claimable");
    }

    private static void PeakOffPeakAndNightTransitions()
    {
        TimetableSchedule schedule = Schedule(first: 0, peak: 5, offPeak: 10, night: 20, terminal: TerminalA);
        TransitTimetablesSetting settings = Settings();

        Equal(10, ScheduleMath.IntervalFor(settings, schedule, default, 6 * 60, LineSchedule.DayAndNight), "night end is exclusive");
        Equal(5, ScheduleMath.IntervalFor(settings, schedule, default, 7 * 60, LineSchedule.DayAndNight), "morning peak start");
        Equal(10, ScheduleMath.IntervalFor(settings, schedule, default, 9 * 60, LineSchedule.DayAndNight), "morning peak end");
        Equal(5, ScheduleMath.IntervalFor(settings, schedule, default, 16 * 60, LineSchedule.DayAndNight), "evening peak start");
        Equal(10, ScheduleMath.IntervalFor(settings, schedule, default, 18 * 60, LineSchedule.DayAndNight), "evening peak end");
        Equal(20, ScheduleMath.IntervalFor(settings, schedule, default, 22 * 60, LineSchedule.DayAndNight), "night start");
        Equal(10, ScheduleMath.IntervalFor(settings, schedule, default, 23 * 60, LineSchedule.Day), "day-only ignores night interval");
        Equal(20, ScheduleMath.IntervalFor(settings, schedule, default, 12 * 60, LineSchedule.Night), "night-only uses night interval");
    }

    private static void CustomPeakTransition()
    {
        TimetableSchedule schedule = Schedule(first: 0, peak: 5, offPeak: 10, night: 20, terminal: TerminalA);
        var custom = new CustomPeakSchedule
        {
            m_Enabled = true,
            m_Interval = 3,
            m_Start1 = 8,
            m_End1 = 10,
        };

        Equal(3, ScheduleMath.IntervalFor(Settings(), schedule, custom, 8 * 60, LineSchedule.DayAndNight), "custom peak start");
        Equal(10, ScheduleMath.IntervalFor(Settings(), schedule, custom, 10 * 60, LineSchedule.DayAndNight), "custom peak end");
    }

    private static void EarlyVehicleAtTerminalA()
    {
        uint target = TerminalTimingMath.DepartureTarget(900, 1000, hasSlot: true);

        Equal(1000u, target, "A scheduled target");
        True(TerminalTimingMath.ShouldHold(900, target, overrun: false), "early A vehicle is held");
    }

    private static void EarlyVehicleAtTerminalB()
    {
        int aSlot = 600;
        int bOffset = 45;
        Equal(645, TerminalTimingMath.TimingPointDepartureMinute(aSlot, bOffset), "B derives from A slot");

        uint target = TerminalTimingMath.DepartureTarget(640, 645, hasSlot: true);
        Equal(645u, target, "B scheduled target");
        True(TerminalTimingMath.ShouldHold(640, target, overrun: false), "early B vehicle is held");
    }

    private static void TerminalBLayoverIsNotClamped()
    {
        int legacyBound = TerminalTimingMath.TimingPointHoldBound(
            maxInterval: 5,
            ownedWait: 60,
            maxSlotSteps: 3,
            slackMinutes: 15,
            layoverMinutes: 0);
        int bound = TerminalTimingMath.TimingPointHoldBound(
            maxInterval: 5,
            ownedWait: 60,
            maxSlotSteps: 3,
            slackMinutes: 15,
            layoverMinutes: 60);

        Equal(35, legacyBound, "Terminal A timing-point bound remains unchanged without a layover");
        True(bound >= 60, "configured 60-minute B layover fits the timing-point safety bound");
        True(TerminalTimingMath.ShouldHold(100, 160, overrun: 60 > bound), "legitimate B layover remains held");
    }

    private static void LateVehicleIsNotHeld()
    {
        uint target = TerminalTimingMath.DepartureTarget(650, 645, hasSlot: true);

        Equal(650u, target, "late arrival becomes release target");
        False(TerminalTimingMath.ShouldHold(650, target, overrun: false), "late vehicle is released");
        False(TerminalTimingMath.ShouldHold(640, 645, overrun: true), "overrun prevents an unnecessary hold");
        Equal(650u, TerminalTimingMath.DepartureTarget(650, 700, hasSlot: false), "slotless vehicle is not held");
    }

    private static void TimetableSlotProgression()
    {
        TimetableSchedule schedule = Schedule(first: 360, peak: 5, offPeak: 10, night: 20, terminal: TerminalA);
        int[] departures = new int[4];
        Equal(4, ScheduleMath.Upcoming(Settings(), schedule, default, LineSchedule.DayAndNight, 410, departures, 4), "slot progression count");

        Sequence(new[] { 410, 420, 425, 430 }, departures, "off-peak to peak slots");
        True(TerminalTimingMath.CandidateWithinAssignmentWindow(12, 12, 0), "first slot window boundary");
        False(TerminalTimingMath.CandidateWithinAssignmentWindow(13, 12, 0), "first slot outside window");
        True(TerminalTimingMath.CandidateWithinAssignmentWindow(24, 12, 1), "later slot window progression");
    }

    private static void NoDuplicateTerminalSemantics()
    {
        False(LineTerminalSelection.CanConfigureSecondary(TerminalA, TerminalA), "duplicate configuration");
        True(TerminalTimingMath.SecondaryShouldAcquireSlot(hasSlot: false), "slotless vehicle may recover at B");
        False(TerminalTimingMath.SecondaryShouldAcquireSlot(hasSlot: true), "assigned vehicle keeps its single slot");

        int sharedSlot = 720;
        Equal(720, TerminalTimingMath.TimingPointDepartureMinute(sharedSlot, 0), "A uses shared slot origin");
        Equal(765, TerminalTimingMath.TimingPointDepartureMinute(sharedSlot, 45), "B is an offset on the same slot");
        Equal(30, TerminalTimingMath.OffsetFromSlotOrigin(90, 60), "downstream route offset remains relative to B");
    }

    private static void DefaultScheduleState()
    {
        TimetableSchedule schedule = TimetableSchedule.Default();

        False(schedule.m_Enabled, "enabled");
        Equal((ushort)300, schedule.m_FirstDeparture, "first departure");
        Equal((ushort)8, schedule.m_PeakInterval, "peak interval");
        Equal((ushort)12, schedule.m_OffPeakInterval, "off-peak interval");
        Equal((ushort)30, schedule.m_NightInterval, "night interval");
        Equal(Entity.Null, schedule.m_TerminusStop, "Terminal A default");
        Equal(Entity.Null, LineTerminalB.Default().m_Stop, "Terminal B default");
    }

    private static void ScheduleSerializationOrderRemainsCompatible()
    {
        TimetableSchedule schedule = Schedule(first: 301, peak: 7, offPeak: 13, night: 31, terminal: TerminalA);
        schedule.m_Enabled = true;
        var writer = new RecordingWriter();

        schedule.Serialize(writer);

        Equal(6, writer.Values.Count, "legacy payload field count");
        Equal(true, writer.Values[0], "field 0 enabled");
        Equal((ushort)301, writer.Values[1], "field 1 first departure");
        Equal((ushort)7, writer.Values[2], "field 2 peak interval");
        Equal((ushort)13, writer.Values[3], "field 3 off-peak interval");
        Equal((ushort)31, writer.Values[4], "field 4 night interval");
        Equal(TerminalA, writer.Values[5], "field 5 Terminal A");
    }

    private static void ScheduleSerializationRoundTrip()
    {
        TimetableSchedule original = Schedule(first: 111, peak: 4, offPeak: 9, night: 25, terminal: TerminalA);
        original.m_Enabled = true;
        var writer = new RecordingWriter();
        original.Serialize(writer);

        TimetableSchedule restored = default;
        restored.Deserialize(new RecordingReader(writer.Values));

        Equal(original.m_Enabled, restored.m_Enabled, "enabled round trip");
        Equal(original.m_FirstDeparture, restored.m_FirstDeparture, "first round trip");
        Equal(original.m_PeakInterval, restored.m_PeakInterval, "peak round trip");
        Equal(original.m_OffPeakInterval, restored.m_OffPeakInterval, "off-peak round trip");
        Equal(original.m_NightInterval, restored.m_NightInterval, "night round trip");
        Equal(original.m_TerminusStop, restored.m_TerminusStop, "Terminal A round trip");
    }

    private static void LegacySchedulePayloadDeserializes()
    {
        object[] legacyPayload =
        {
            true,
            (ushort)222,
            (ushort)6,
            (ushort)11,
            (ushort)29,
            TerminalA,
        };

        TimetableSchedule restored = default;
        restored.Deserialize(new RecordingReader(legacyPayload));

        True(restored.m_Enabled, "legacy enabled");
        Equal((ushort)222, restored.m_FirstDeparture, "legacy first departure");
        Equal((ushort)6, restored.m_PeakInterval, "legacy peak interval");
        Equal((ushort)11, restored.m_OffPeakInterval, "legacy off-peak interval");
        Equal((ushort)29, restored.m_NightInterval, "legacy night interval");
        Equal(TerminalA, restored.m_TerminusStop, "legacy Terminal A");
    }

    private static void TerminalBSerializationIsSeparateAndVersioned()
    {
        var second = new LineTerminalB { m_Stop = TerminalB };
        var writer = new RecordingWriter();

        second.Serialize(writer);

        Equal(2, writer.Values.Count, "Terminal B payload field count");
        Equal((byte)1, writer.Values[0], "Terminal B payload version");
        Equal(TerminalB, writer.Values[1], "Terminal B stop");

        LineTerminalB restored = default;
        restored.Deserialize(new RecordingReader(writer.Values));
        Equal(TerminalB, restored.m_Stop, "Terminal B round trip");

        var scheduleWriter = new RecordingWriter();
        Schedule(terminal: TerminalA).Serialize(scheduleWriter);
        Equal(6, scheduleWriter.Values.Count, "Terminal B is not appended to legacy schedule payload");
    }

    private static void TerminalBVersionZeroDefaultsToNull()
    {
        object[] versionZeroPayload = { (byte)0 };
        var restored = new LineTerminalB { m_Stop = TerminalB };

        restored.Deserialize(new RecordingReader(versionZeroPayload));

        Equal(Entity.Null, restored.m_Stop, "unknown legacy payload has no Terminal B");
    }

    private static TimetableSchedule Schedule(
        ushort first = 300,
        ushort peak = 8,
        ushort offPeak = 12,
        ushort night = 30,
        Entity terminal = default)
    {
        return new TimetableSchedule
        {
            m_Enabled = true,
            m_FirstDeparture = first,
            m_PeakInterval = peak,
            m_OffPeakInterval = offPeak,
            m_NightInterval = night,
            m_TerminusStop = terminal,
        };
    }

    private static TransitTimetablesSetting Settings() => new TransitTimetablesSetting();

    private static void True(bool value, string name)
    {
        if (!value)
            throw new InvalidOperationException($"{name}: expected true, got false");
    }

    private static void False(bool value, string name)
    {
        if (value)
            throw new InvalidOperationException($"{name}: expected false, got true");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    private static void Sequence(IReadOnlyList<int> expected, IReadOnlyList<int> actual, string name)
    {
        Equal(expected.Count, actual.Count, $"{name} count");
        for (int i = 0; i < expected.Count; i++)
            Equal(expected[i], actual[i], $"{name} item {i}");
    }
}
