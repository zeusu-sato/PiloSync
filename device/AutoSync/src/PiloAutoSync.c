/* Schedule HotSync. AlarmTriggered can arrive during another app's dialog,
 * so it only reschedules. On Palm OS 3.1 through 5.x, DisplayAlarm arrives
 * from the foreground app's SysHandleEvent when UI is safe; its queued cradle
 * key is handled by the next EvtGetEvent/SysHandleEvent like the physical key.
 * An alarm also wakes a powered-off device for this same sequence.
 * Always try outside the cradle: SysBatteryInfo pluggedIn is unreliable on
 * battery-powered devices such as Visor Deluxe and m100. */
#include <Pilot.h>
#include <SysEvtMgr.h>
#include "Schedule.h"
#include "PiloAutoSyncRsc.h"
#define PREF_VERSION 1

static void LoadSchedule(Schedule *s)
{
    UInt16 size = sizeof(*s);
    Int16 version = PrefGetAppPreferences(APP_CREATOR, 0, s, &size, true);
    if (version != PREF_VERSION || size != sizeof(*s)) MemSet(s, sizeof(*s), 0);
    schedule_validate(s);
}
static void Reserve(const Schedule *s, UInt32 after)
{
    DmSearchStateType search;
    UInt16 card;
    LocalID db;
    if (!DmGetNextDatabaseByTypeCreator(true, &search, 'appl', APP_CREATOR, true, &card, &db))
        AlmSetAlarm(card, db, 0, schedule_next(s, after), true);
}
static void Save(Schedule *s)
{
    PrefSetAppPreferences(APP_CREATOR, 0, PREF_VERSION, s, sizeof(*s), true);
    Reserve(s, TimGetSeconds());
}
static void StartSync(void)
{
#ifdef START_APPSWITCH
    DmSearchStateType search;
    UInt16 card;
    LocalID db;
    if (!DmGetNextDatabaseByTypeCreator(true, &search, 'appl', 'sync', true, &card, &db))
        SysUIAppSwitch(card, db, sysAppLaunchCmdSyncRequestLocal, NULL);
#else
    EvtEnqueueKey(vchrHardCradle, 0, commandKeyMask);
#endif
}
static void *Object(FormType *f, UInt16 id)
{
    return FrmGetObjectPtr(f, FrmGetObjectIndex(f, id));
}
static void RunUI(void)
{
    Schedule s;
    FormType *f, *dialog;
    ListType *list;
    EventType event;
    DateTimeType dt;
    UInt16 day, i, id, result, mask;
    Int16 selection, hour, minute;
    UInt32 next;
    Boolean done = false;
    Char labels[8][8], *choices[8], nextLabel[48];
#if APP_LANGUAGE_JAPANESE
    const Char *days[] = { "\223\372", "\214\216", "\211\316", "\220\205", "\226\330", "\213\340", "\223\171" };
    Char *title = "\216\236\215\217";
#else
    const Char *days[] = {"Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"};
    Char *title = "Time";
#endif
    LoadSchedule(&s);
    TimSecondsToDateTime(TimGetSeconds(), &dt);
    day = dt.weekDay;
    f = FrmInitForm(MainForm);
    FrmSetActiveForm(f);
    list = (ListType *)Object(f, TimeList);
    while (!done) {
        CtlSetValue((ControlType *)Object(f, EnableControl), s.enabled);
        for (i = 0; i < 7; ++i) CtlSetValue((ControlType *)Object(f, DayControl + i), i == day);
        for (i = 0; i < s.count[day]; ++i) {
            StrPrintF(labels[i], "%u:%02u", s.times[day][i] / 60, s.times[day][i] % 60);
            choices[i] = labels[i];
        }
        LstSetListChoices(list, choices, s.count[day]);
        LstSetSelection(list, noListSelection);
        next = schedule_next(&s, TimGetSeconds());
        if (next) {
            TimSecondsToDateTime(next, &dt);
#if APP_LANGUAGE_JAPANESE
            StrPrintF(nextLabel, "\216\237\211\361\201\106" "%s %u:%02u", days[dt.weekDay], dt.hour, dt.minute);
        } else StrCopy(nextLabel, "\216\237\211\361\201\106\202\310\202\265");
#else
            StrPrintF(nextLabel, "Next: %s %u:%02u", days[dt.weekDay], dt.hour, dt.minute);
        } else StrCopy(nextLabel, "Next: none");
#endif
        FrmCopyLabel(f, NextLabel, nextLabel);
        FrmDrawForm(f);
        for (;;) {
            EvtGetEvent(&event, evtWaitForever);
            if (SysHandleEvent(&event) || MenuHandleEvent(NULL, &event, NULL)) continue;
            if (event.eType == appStopEvent) { done = true; Save(&s); break; }
            if (event.eType == ctlSelectEvent) {
                id = event.data.ctlSelect.controlID;
                if (id >= DayControl && id < DayControl + 7) { day = id - DayControl; break; }
                if (id == EnableControl) { s.enabled = CtlGetValue((ControlType *)Object(f, id)); Save(&s); break; }
                if (id == AddControl) {
                    selection = LstGetSelection(list);
                    hour = 6; minute = 0;
                    if (selection != (Int16)noListSelection) { hour = s.times[day][selection] / 60; minute = s.times[day][selection] % 60; }
                    if (SelectOneTime(&hour, &minute, title)) {
                        for (i = 0; i < s.count[day]; ++i) if (s.times[day][i] == (UInt16)(hour * 60 + minute)) break;
                        if (i < s.count[day]) FrmAlert(DuplicateAlert);
                        else if (s.count[day] == MAX_TIMES) FrmAlert(FullAlert);
                        else { schedule_add(&s, day, hour * 60 + minute); Save(&s); }
                    }
                    break;
                }
                if (id == DeleteControl) {
                    selection = LstGetSelection(list);
                    if (selection != (Int16)noListSelection) { schedule_delete(&s, day, selection); Save(&s); }
                    break;
                }
                if (id == CopyControl) {
                    dialog = FrmInitForm(CopyForm);
                    FrmHideObject(dialog, FrmGetObjectIndex(dialog, CopyDay + day));
                    result = FrmDoDialog(dialog);
                    if (result == CopyOK) {
                        mask = 0;
                        for (i = 0; i < 7; ++i) if (i != day && CtlGetValue((ControlType *)Object(dialog, CopyDay + i))) mask |= 1U << i;
                        schedule_copy(&s, day, mask); Save(&s);
                    }
                    FrmDeleteForm(dialog);
                    FrmSetActiveForm(f);
                    break;
                }
            }
            FrmHandleEvent(f, &event);
        }
    }
    FrmCloseAllForms();
}
UInt32 PilotMain(UInt16 cmd, Ptr param, UInt16 flags);
UInt32 PilotMain(UInt16 cmd, Ptr param, UInt16 flags)
{
    Schedule s;
    UInt32 rom, after;
    (void)flags;
    if (FtrGet(sysFtrCreator, sysFtrNumROMVersion, &rom) || rom < sysMakeROMVersion(3,1,0,sysROMStageRelease,0)) {
        if (cmd == sysAppLaunchCmdNormalLaunch) { FrmAlert(RomIncompatibleAlert); return sysErrRomIncompatible; }
        return 0;
    }
    switch (cmd) {
    case sysAppLaunchCmdNormalLaunch: RunUI(); break;
    case sysAppLaunchCmdSystemReset:
    case sysAppLaunchCmdTimeChange:
    case sysAppLaunchCmdSyncNotify: LoadSchedule(&s); Reserve(&s, TimGetSeconds()); break;
    case sysAppLaunchCmdAlarmTriggered:
        LoadSchedule(&s); after = TimGetSeconds();
        if (((SysAlarmTriggeredParamType *)param)->alarmSeconds > after) after = ((SysAlarmTriggeredParamType *)param)->alarmSeconds;
        Reserve(&s, after); ((SysAlarmTriggeredParamType *)param)->purgeAlarm = false; break;
    case sysAppLaunchCmdDisplayAlarm:
        EvtResetAutoOffTimer(); LoadSchedule(&s); if (s.enabled) StartSync(); break;
    }
    return 0;
}
