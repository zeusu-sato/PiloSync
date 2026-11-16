#ifndef SCHEDULE_H
#define SCHEDULE_H
#define DAYS 7
#define MAX_TIMES 8
/* Times are minutes since midnight; seconds are local seconds since
 * 1904-01-01. Weekdays are numbered 0 = Sunday through 6 = Saturday. */
typedef struct {
    unsigned short count[7];
    unsigned short times[7][8];
    unsigned short enabled;
} Schedule;
int schedule_add(Schedule *, unsigned, unsigned);
int schedule_delete(Schedule *, unsigned, unsigned);
void schedule_copy(Schedule *, unsigned, unsigned);
void schedule_sort(Schedule *, unsigned);
void schedule_validate(Schedule *);
unsigned long schedule_next(const Schedule *, unsigned long);
#endif
