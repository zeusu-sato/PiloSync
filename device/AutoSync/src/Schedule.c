#include "Schedule.h"

void schedule_sort(Schedule *s, unsigned d)
{
    unsigned i, j;
    unsigned short v;
    if (d >= DAYS)
        return;
    if (s->count[d] > MAX_TIMES)
        s->count[d] = MAX_TIMES;
    for (i = 1; i < s->count[d]; ++i) {
        v = s->times[d][i];
        j = i;
        while (j && s->times[d][j - 1] > v) {
            s->times[d][j] = s->times[d][j - 1];
            --j;
        }
        s->times[d][j] = v;
    }
}

int schedule_add(Schedule *s, unsigned d, unsigned minute)
{
    unsigned i;
    if (d >= DAYS || minute >= 1440)
        return 0;
    for (i = 0; i < s->count[d]; ++i)
        if (s->times[d][i] == minute)
            return 0;
    if (s->count[d] >= MAX_TIMES)
        return 0;
    s->times[d][s->count[d]++] = (unsigned short)minute;
    schedule_sort(s, d);
    return 1;
}

int schedule_delete(Schedule *s, unsigned d, unsigned index)
{
    unsigned i;
    if (d >= DAYS || index >= s->count[d])
        return 0;
    for (i = index + 1; i < s->count[d]; ++i)
        s->times[d][i - 1] = s->times[d][i];
    --s->count[d];
    return 1;
}

void schedule_copy(Schedule *s, unsigned source, unsigned mask)
{
    unsigned d, i;
    if (source >= DAYS)
        return;
    for (d = 0; d < DAYS; ++d) {
        if (d != source && (mask & (1U << d))) {
            s->count[d] = s->count[source];
            for (i = 0; i < s->count[source]; ++i)
                s->times[d][i] = s->times[source][i];
        }
    }
}

void schedule_validate(Schedule *s)
{
    unsigned d, i, n;
    s->enabled = s->enabled ? 1 : 0;
    for (d = 0; d < DAYS; ++d) {
        schedule_sort(s, d);
        n = 0;
        for (i = 0; i < s->count[d]; ++i)
            if (s->times[d][i] < 1440 && (!n || s->times[d][i] != s->times[d][n - 1]))
                s->times[d][n++] = s->times[d][i];
        s->count[d] = (unsigned short)n;
    }
}

unsigned long schedule_next(const Schedule *s, unsigned long after)
{
    unsigned long day, base, candidate;
    unsigned offset, d, i;
    if (!s->enabled || after > 0xffffffffUL)
        return 0;
    day = after / 86400UL;
    base = day * 86400UL;
    for (offset = 0; offset <= 7; ++offset) {
        d = (unsigned)((day + offset + 5) % 7);
        for (i = 0; i < s->count[d] && i < MAX_TIMES; ++i) {
            unsigned long delta = (unsigned long)offset * 86400UL + (unsigned long)s->times[d][i] * 60UL;
            if (delta > 0xffffffffUL - base)
                continue;
            candidate = base + delta;
            if (candidate > after)
                return candidate;
        }
    }
    return 0;
}
