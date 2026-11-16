#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "Schedule.h"
int main(void) {
    Schedule s; unsigned i; unsigned long mon = 3876422400UL;
    memset(&s,0,sizeof(s)); s.enabled=1;
    assert(schedule_next(&s,0)==0);
    assert(schedule_add(&s,5,390)); assert(schedule_add(&s,5,60));
    assert(!schedule_add(&s,5,60)); assert(s.times[5][0]==60);
    assert(schedule_next(&s,0)==3600); assert(schedule_next(&s,3600)==23400);
    assert(schedule_next(&s,23400)==604800UL+3600);
    schedule_copy(&s,5,1U<<6); assert(s.count[6]==2);
    assert(schedule_next(&s,23401)==86400UL+3600);
    assert(schedule_delete(&s,6,0)); assert(!schedule_delete(&s,6,4));
    for(i=0;i<6;i++) assert(schedule_add(&s,5,100+i));
    assert(!schedule_add(&s,5,200));
    s.count[0]=4; s.times[0][0]=1500; s.times[0][1]=3; s.times[0][2]=2; s.times[0][3]=3;
    schedule_validate(&s); assert(s.count[0]==2 && s.times[0][0]==2 && s.times[0][1]==3);
    memset(&s,0,sizeof(s)); s.enabled=1; assert(schedule_add(&s,1,390));
    assert(schedule_next(&s,mon)==mon+23400); s.enabled=0; assert(!schedule_next(&s,mon));
    /* Calculate 2026-11-02 from the Palm epoch, including leap years. */
    {
        unsigned year, month;
        unsigned long days = 0;
        static const unsigned lengths[] = {31,28,31,30,31,30,31,31,30,31,30,31};
        for (year = 1904; year < 2026; ++year)
            days += 365 + (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0));
        for (month = 0; month < 10; ++month)
            days += lengths[month];
        mon = (days + 1) * 86400UL;
        memset(&s, 0, sizeof(s));
        s.enabled = 1;
        assert(schedule_add(&s, 1, 1));
        assert(schedule_next(&s, mon) == mon + 60);
        assert(schedule_next(&s, mon + 60) == mon + 7 * 86400UL + 60);
    }
    memset(&s, 0, sizeof(s));
    assert(schedule_add(&s, 1, 300));
    assert(schedule_add(&s, 1, 100));
    assert(schedule_add(&s, 2, 999));
    schedule_copy(&s, 1, (1U << 1) | (1U << 2));
    assert(s.count[1] == 2 && s.times[1][0] == 100 && s.times[1][1] == 300);
    assert(s.count[2] == 2 && s.times[2][0] == 100 && s.times[2][1] == 300);
    assert(schedule_delete(&s, 2, 0) && s.count[2] == 1 && s.times[2][0] == 300);
    s.count[3] = 9;
    for (i = 0; i < 8; ++i) s.times[3][i] = (unsigned short)(7 - i);
    s.enabled = 42;
    schedule_validate(&s);
    assert(s.count[3] == 8 && s.times[3][0] == 0 && s.times[3][7] == 7 && s.enabled == 1);
    assert(!schedule_add(&s, 7, 0) && !schedule_add(&s, 0, 1440));
    puts("schedule tests passed"); return 0;
}
