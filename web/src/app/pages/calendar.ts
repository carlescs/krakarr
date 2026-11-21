import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { debounceTime, merge } from 'rxjs';
import { Api, errorMessage } from '../core/api';
import { Auth } from '../core/auth';
import {
  CalendarFilters, CalendarItem, CalendarStatusFilter, CalendarView, DEFAULT_FILTERS, FeedOptions, ItemState, RELEASE_LABELS, addDays, buildItems, daysBetween,
  feedUrl, groupByDay, itemState, localeFirstDayOfWeek, parseIsoDate, shiftAnchor, toIsoDate, viewRange,
} from '../core/calendar';
import { Events } from '../core/events';
import { CalendarEntry, Subscriber } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from '../shared/modal';
import { Poster } from '../shared/poster';

const STORAGE_KEY = 'krakarr.calendar';

type StyleKey = 'episodes' | 'Theatrical' | 'Digital' | 'Physical';

/** Colour per kind of release: left bar, chip background and legend dot. */
const STYLES: Record<StyleKey, { bar: string; chip: string; dot: string; label: string }> = {
  episodes: { bar: 'border-l-accent-400', chip: 'bg-accent-500/10 hover:bg-accent-500/20', dot: 'bg-accent-400', label: 'Episodes' },
  Theatrical: { bar: 'border-l-amber-400', chip: 'bg-amber-500/10 hover:bg-amber-500/20', dot: 'bg-amber-400', label: RELEASE_LABELS.Theatrical },
  Digital: { bar: 'border-l-violet-400', chip: 'bg-violet-500/10 hover:bg-violet-500/20', dot: 'bg-violet-400', label: RELEASE_LABELS.Digital },
  Physical: { bar: 'border-l-pink-400', chip: 'bg-pink-500/10 hover:bg-pink-500/20', dot: 'bg-pink-400', label: RELEASE_LABELS.Physical },
};

const STATES: Record<ItemState, { label: string; badge: string; icon: string }> = {
  downloaded: { label: 'Downloaded', badge: 'bg-emerald-500/15 text-emerald-300', icon: '✓' },
  downloading: { label: 'Downloading', badge: 'bg-accent-500/15 text-accent-300', icon: '↓' },
  missing: { label: 'Missing', badge: 'bg-rose-500/15 text-rose-300', icon: '!' },
  upcoming: { label: 'Upcoming', badge: 'bg-surface-700 text-slate-300', icon: '' },
  unmonitored: { label: 'Unmonitored', badge: 'bg-slate-500/15 text-slate-400', icon: '' },
};

/** Whose titles the calendar shows: the whole library, the signed-in user's followed titles, or (staff) another user's. */
type Owner = 'all' | 'me' | number;

interface Stored { view?: CalendarView; filters?: Partial<CalendarFilters>; feed?: Partial<FeedOptions>; owner?: Owner; }

function readStored(): Stored {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}') as Stored; } catch { return {}; }
}

@Component({
  selector: 'kr-calendar',
  imports: [FormsModule, RouterLink, NgTemplateOutlet, Modal, Poster],
  template: `
    <!-- Toolbar -->
    <div class="mb-4 flex flex-wrap items-center gap-x-2 gap-y-2 sm:gap-x-3">
      <h1 class="page-title max-sm:hidden">Calendar</h1>
      <div class="flex items-center gap-1">
        <button class="btn-ghost px-2.5" (click)="move(-1)" [attr.aria-label]="'Previous ' + periodName()">‹</button>
        <button class="btn-secondary btn-sm" (click)="goToday()">Today</button>
        <button class="btn-ghost px-2.5" (click)="move(1)" [attr.aria-label]="'Next ' + periodName()">›</button>
      </div>
      <span class="text-base font-semibold text-slate-100">{{ rangeLabel() }}</span>
      @if (loading()) { <span class="size-4 animate-spin rounded-full border-2 border-surface-600 border-t-accent-400" aria-label="Loading"></span> }
      <div class="flex-1"></div>
      <select class="input !w-auto !py-1.5 text-xs sm:text-xs max-sm:max-w-40" aria-label="Whose titles" [ngModel]="owner()" (ngModelChange)="owner.set($event)">
        <option value="me">My titles</option>
        <option value="all">Everything</option>
        @for (u of subscribers(); track u.id) {
          <option [ngValue]="u.id">{{ u.username }}'s titles</option>
        }
      </select>
      <div class="inline-flex rounded-lg border border-surface-700 bg-surface-900 p-0.5" role="tablist" aria-label="View">
        @for (v of views; track v.id) {
          <button class="rounded-md px-3 py-1.5 text-xs font-medium transition pointer-coarse:min-h-9 max-[359px]:px-2"
                  [class]="view() === v.id ? 'bg-surface-700 text-white' : 'text-slate-400 hover:text-slate-200'"
                  role="tab" [attr.aria-selected]="view() === v.id" (click)="setView(v.id)">{{ v.label }}</button>
        }
      </div>
      <button class="btn-secondary btn-sm relative gap-1.5 max-sm:ml-auto sm:hidden" (click)="filtersOpen.set(true)" [attr.aria-label]="'Filters, ' + activeFilterCount() + ' active'">
        <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="1.8" stroke="currentColor" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" d="M3 5h18M6 12h12M10 19h4" /></svg>
        <span class="max-[359px]:hidden">Filters</span>
        @if (activeFilterCount() > 0) {
          <span class="flex size-4 items-center justify-center rounded-full bg-accent-500 text-[10px] font-bold text-slate-950">{{ activeFilterCount() }}</span>
        }
      </button>
      <!-- The feed URL embeds the API key, which only admins may see. -->
      @if (auth.isAdmin()) {
        <button class="btn-ghost btn-sm" (click)="openFeed()" title="Subscribe from another calendar app" aria-label="iCal feed">
          <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="1.8" stroke="currentColor"><path stroke-linecap="round" stroke-linejoin="round" d="M12.75 19.5v-.75a7.5 7.5 0 0 0-7.5-7.5H4.5m0-6.75h.75c7.87 0 14.25 6.38 14.25 14.25v.75M6 18.75a.75.75 0 1 1-1.5 0 .75.75 0 0 1 1.5 0Z" /></svg>
          <span class="max-sm:hidden">iCal</span>
        </button>
      }
    </div>

    <!-- Filters on phones: what is narrowed down, with a quick reset -->
    @if (activeFilterCount() > 0) {
      <div class="-mt-1 mb-4 flex items-center gap-2 text-xs sm:hidden">
        <button class="flex min-w-0 flex-1 items-center gap-1.5 text-left" (click)="filtersOpen.set(true)">
          @for (s of activeKinds(); track s) { <span class="size-2 shrink-0 rounded-full" [class]="styles[s].dot"></span> }
          <span class="truncate text-slate-400">{{ filterSummary() }}</span>
        </button>
        @if (hiddenCount() > 0) { <span class="shrink-0 text-slate-500">{{ hiddenCount() }} hidden</span> }
        <button class="shrink-0 px-1 py-1.5 font-medium text-accent-300" (click)="resetFilters()">Reset</button>
      </div>
    }

    <!-- Filters: inline on larger screens -->
    <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2 max-sm:hidden">
      <div class="flex flex-wrap items-center gap-1.5" role="group" aria-label="Show">
        <button [class]="chip(filters().series)" (click)="toggle('series')" [attr.aria-pressed]="filters().series">
          <span class="size-2 rounded-full" [class]="styles.episodes.dot"></span> Series
          <span class="tabular-nums text-slate-500">{{ counts().episodes }}</span>
        </button>
        <div class="inline-flex items-center rounded-full border transition"
             [class]="filters().movies ? 'border-surface-600 bg-surface-800/60' : 'border-surface-700'">
          <button class="inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-medium transition"
                  [class]="filters().movies ? 'bg-surface-700 text-white' : 'text-slate-500 hover:text-slate-300'"
                  (click)="toggle('movies')" [attr.aria-pressed]="filters().movies">
            Movies <span class="tabular-nums text-slate-500">{{ counts().movies }}</span>
          </button>
          @for (t of releaseTypes; track t.key) {
            <button class="inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium transition disabled:cursor-not-allowed disabled:opacity-40"
                    [class]="filters().movies && filters()[t.key] ? 'text-slate-100' : 'text-slate-500 hover:text-slate-300'"
                    [disabled]="!filters().movies" (click)="toggle(t.key)" [attr.aria-pressed]="filters().movies && filters()[t.key]">
              <span class="size-2 rounded-full transition" [class]="filters()[t.key] ? styles[t.style].dot : 'bg-surface-600'"></span>
              {{ styles[t.style].label }}
            </button>
          }
        </div>
      </div>
      <div class="inline-flex rounded-lg border border-surface-700 bg-surface-900 p-0.5" role="radiogroup" aria-label="Status">
        @for (s of statuses; track s.id) {
          <button class="rounded-md px-2.5 py-1 text-xs font-medium transition"
                  [class]="filters().status === s.id ? 'bg-surface-700 text-white' : 'text-slate-400 hover:text-slate-200'"
                  role="radio" [attr.aria-checked]="filters().status === s.id" (click)="setFilter('status', s.id)">{{ s.label }}</button>
        }
      </div>
      <button [class]="chip(filters().unmonitored)" (click)="toggle('unmonitored')" [attr.aria-pressed]="filters().unmonitored">
        <span class="flex size-3.5 items-center justify-center rounded-sm border text-[9px] leading-none"
              [class]="filters().unmonitored ? 'border-accent-400 bg-accent-500 text-slate-950' : 'border-surface-600'">{{ filters().unmonitored ? '✓' : '' }}</span>
        Unmonitored
      </button>
      @if (hiddenCount() > 0 || activeFilterCount() > 0) {
        <div class="ml-auto flex items-center gap-2 text-xs text-slate-500">
          @if (hiddenCount() > 0) { <span>{{ hiddenCount() }} hidden by filters</span> }
          @if (activeFilterCount() > 0) { <button class="text-accent-300 hover:text-accent-200" (click)="resetFilters()">Reset</button> }
        </div>
      }
    </div>

    @if (filtersOpen()) {
      <kr-modal title="Filters" (closed)="filtersOpen.set(false)">
        <div class="space-y-5">
          <section>
            <h3 class="label">Show</h3>
            <div class="divide-y divide-surface-800 overflow-hidden rounded-lg border border-surface-700">
              <ng-container *ngTemplateOutlet="switchRow; context: { key: 'series', label: 'Series', dot: styles.episodes.dot, count: counts().episodes }" />
              <ng-container *ngTemplateOutlet="switchRow; context: { key: 'movies', label: 'Movies', count: counts().movies }" />
              @for (t of releaseTypes; track t.key) {
                <ng-container *ngTemplateOutlet="switchRow; context: { key: t.key, label: styles[t.style].label, dot: styles[t.style].dot, count: counts()[t.style], nested: true }" />
              }
            </div>
          </section>
          <section>
            <h3 class="label">Status</h3>
            <div class="grid grid-cols-3 rounded-lg border border-surface-700 bg-surface-950 p-1" role="radiogroup" aria-label="Status">
              @for (s of statuses; track s.id) {
                <button class="min-h-10 rounded-md px-1 text-sm font-medium transition max-[359px]:text-xs"
                        [class]="filters().status === s.id ? 'bg-surface-700 text-white' : 'text-slate-400'"
                        role="radio" [attr.aria-checked]="filters().status === s.id" (click)="setFilter('status', s.id)">{{ s.label }}</button>
              }
            </div>
          </section>
          <section class="overflow-hidden rounded-lg border border-surface-700">
            <ng-container *ngTemplateOutlet="switchRow; context: { key: 'unmonitored', label: 'Include unmonitored', hint: 'Movies and episodes that are not searched for automatically' }" />
          </section>
        </div>
        <ng-container footer>
          <span class="mr-auto self-center text-xs text-slate-500">{{ items().length }} shown{{ hiddenCount() > 0 ? ' · ' + hiddenCount() + ' hidden' : '' }}</span>
          <button class="btn-ghost" (click)="resetFilters()" [disabled]="activeFilterCount() === 0">Reset</button>
          <button class="btn-primary" (click)="filtersOpen.set(false)">Done</button>
        </ng-container>
      </kr-modal>
    }

    <ng-template #switchRow let-key="key" let-label="label" let-dot="dot" let-count="count" let-nested="nested" let-hint="hint">
      @let on = isOn(key);
      @let disabled = nested && !filters().movies;
      <button class="flex min-h-12 w-full items-center gap-3 px-3 py-2 text-left text-sm transition active:bg-surface-800 disabled:opacity-40"
              [class.pl-8]="nested" [disabled]="disabled" role="switch" [attr.aria-checked]="on" (click)="toggle(key)">
        @if (dot) { <span class="size-2.5 shrink-0 rounded-full" [class]="dot"></span> }
        <span class="min-w-0 flex-1">
          <span class="block" [class]="on && !disabled ? 'text-slate-100' : 'text-slate-400'">{{ label }}</span>
          @if (hint) { <span class="block text-xs text-slate-500">{{ hint }}</span> }
        </span>
        @if (count !== undefined) { <span class="text-xs tabular-nums text-slate-500">{{ count }}</span> }
        <span class="relative h-6 w-10 shrink-0 rounded-full transition" [class]="on ? 'bg-accent-500' : 'bg-surface-700'">
          <span class="absolute top-0.5 left-0.5 size-5 rounded-full bg-white shadow transition-transform" [class.translate-x-4]="on"></span>
        </span>
      </button>
    </ng-template>

    @if (error()) {
      <p class="mb-4 rounded-lg bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{{ error() }}</p>
    }

    <!-- Month -->
    @if (view() === 'month') {
      <div class="card overflow-hidden">
        <div class="grid grid-cols-7 border-b border-surface-700 bg-surface-850 text-center text-[11px] font-semibold uppercase tracking-wide text-slate-500">
          @for (w of weekdays(); track $index) { <div class="py-2"><span class="sm:hidden">{{ w.narrow }}</span><span class="max-sm:hidden">{{ w.short }}</span></div> }
        </div>
        <div class="grid grid-cols-7">
          @for (day of days(); track day) {
            @let list = byDay().get(day) ?? [];
            <div class="min-h-16 cursor-pointer border-r border-b border-surface-800 p-1 transition hover:bg-surface-850 sm:min-h-28 sm:p-1.5 [&:nth-child(7n)]:border-r-0"
                 [class.bg-surface-950]="!inMonth(day)" [class.!bg-surface-800]="selectedDay() === day"
                 (click)="selectDay(day)" role="button" [attr.aria-label]="longDate(day) + ': ' + list.length + ' release(s)'">
              <div class="mb-1 flex items-center">
                <span class="flex size-6 items-center justify-center rounded-full text-xs font-medium"
                      [class]="day === today ? 'bg-accent-500 text-slate-950' : inMonth(day) ? 'text-slate-300' : 'text-slate-600'">{{ dayNumber(day) }}</span>
              </div>
              <!-- Phones: dots -->
              <div class="flex flex-wrap gap-1 px-0.5 sm:hidden">
                @for (item of list.slice(0, 6); track item.key) {
                  <span class="size-1.5 rounded-full" [class]="styles[styleKey(item)].dot" [class.opacity-40]="state(item) === 'unmonitored'"></span>
                }
                @if (list.length > 6) { <span class="text-[9px] leading-none text-slate-500">+{{ list.length - 6 }}</span> }
              </div>
              <!-- Larger screens: chips -->
              <div class="space-y-0.5 max-sm:hidden">
                @for (item of list.slice(0, maxChips); track item.key) {
                  <a [routerLink]="item.link" (click)="$event.stopPropagation()" [title]="tooltip(item)"
                     class="flex items-center gap-1 truncate rounded border-l-2 px-1.5 py-0.5 text-xs text-slate-200"
                     [class]="styles[styleKey(item)].bar + ' ' + styles[styleKey(item)].chip" [class.opacity-50]="state(item) === 'unmonitored'">
                    @if (states[state(item)].icon) {
                      <span class="shrink-0 font-bold" [class]="stateText(item)">{{ states[state(item)].icon }}</span>
                    }
                    <span class="truncate">{{ item.title }}</span>
                    <span class="shrink-0 text-[10px] text-slate-400">{{ item.kind === 'episodes' ? item.subtitle : '' }}</span>
                  </a>
                }
                @if (list.length > maxChips) {
                  <button class="w-full rounded px-1.5 text-left text-[11px] text-slate-400 hover:text-white" (click)="selectDay(day); $event.stopPropagation()">
                    +{{ list.length - maxChips }} more
                  </button>
                }
              </div>
            </div>
          }
        </div>
      </div>

      @if (selectedDay(); as day) {
        <section class="card mt-4 p-4 sm:p-5">
          <div class="mb-3 flex items-center gap-2">
            <h2 class="flex-1 text-sm font-semibold text-slate-200">{{ longDate(day) }}</h2>
            <button class="btn-ghost btn-sm" (click)="selectedDay.set(null)" aria-label="Close day">✕</button>
          </div>
          @for (item of byDay().get(day) ?? []; track item.key) {
            <ng-container *ngTemplateOutlet="row; context: { $implicit: item }" />
          } @empty {
            <p class="text-sm text-slate-500">Nothing on this day.</p>
          }
        </section>
      }
    }

    <!-- Week -->
    @if (view() === 'week') {
      <div class="grid gap-3 lg:grid-cols-7 lg:gap-2">
        @for (day of days(); track day) {
          @let list = byDay().get(day) ?? [];
          <section class="card min-w-0 p-3 lg:min-h-64 lg:p-2" [class.ring-1]="day === today" [class.ring-accent-500]="day === today"
                   [class.max-lg:hidden]="list.length === 0 && day !== today">
            <h2 class="mb-2 flex items-baseline gap-2 px-1 text-xs font-semibold uppercase tracking-wide"
                [class]="day === today ? 'text-accent-300' : 'text-slate-400'">
              <span>{{ weekdayName(day) }}</span><span class="text-lg font-semibold text-slate-100 lg:text-base">{{ dayNumber(day) }}</span>
            </h2>
            <div class="space-y-2">
              @for (item of list; track item.key) {
                <a [routerLink]="item.link" [title]="tooltip(item)"
                   class="flex gap-2 rounded-lg border-l-2 p-1.5 transition lg:flex-col"
                   [class]="styles[styleKey(item)].bar + ' ' + styles[styleKey(item)].chip" [class.opacity-50]="state(item) === 'unmonitored'">
                  <kr-poster class="w-10 shrink-0 lg:hidden" [url]="item.posterUrl" [title]="item.title" />
                  <div class="min-w-0">
                    <div class="line-clamp-2 text-sm font-medium text-slate-100 lg:text-xs">{{ item.title }}</div>
                    <div class="text-xs text-slate-400">{{ item.subtitle }}</div>
                    <div class="truncate text-xs text-slate-500">{{ detail(item) }}</div>
                    @if (state(item) !== 'upcoming') {
                      <span class="badge mt-1 px-1.5 text-[10px]" [class]="states[state(item)].badge">{{ states[state(item)].label }}</span>
                    }
                  </div>
                </a>
              } @empty {
                <p class="px-1 text-xs text-slate-600">Nothing</p>
              }
            </div>
          </section>
        }
      </div>
    }

    <!-- Agenda -->
    @if (view() === 'agenda') {
      @if (agendaDays().length === 0 && !loading()) {
        <div class="card px-6 py-14 text-center text-sm text-slate-400">
          Nothing {{ owner() === 'all' ? '' : owner() === 'me' ? 'you follow ' : 'they follow ' }}in {{ rangeLabel() }}{{ hiddenCount() > 0 ? ' with these filters' : '' }}.
          @if (owner() === 'me') { <span class="mt-1 block text-xs text-slate-500">Follow movies and series from their pages to see them here, or switch to Everything.</span> }
        </div>
      }
      <div class="space-y-4">
        @for (day of agendaDays(); track day) {
          <section [id]="'day-' + day">
            <h2 class="sticky top-[calc(3.5rem+env(safe-area-inset-top))] z-10 -mx-1 mb-2 flex items-baseline gap-2 bg-surface-950/90 px-1 py-1.5 text-sm font-semibold backdrop-blur"
                [class]="day === today ? 'text-accent-300' : day < today ? 'text-slate-500' : 'text-slate-200'">
              {{ longDate(day) }}
              @if (day === today) { <span class="badge bg-accent-500/15 text-accent-300">Today</span> }
            </h2>
            <div class="card divide-y divide-surface-800 px-3 sm:px-4">
              @for (item of byDay().get(day) ?? []; track item.key) {
                <ng-container *ngTemplateOutlet="row; context: { $implicit: item }" />
              }
            </div>
          </section>
        }
      </div>
    }

    <!-- Shared row (agenda and selected day) -->
    <ng-template #row let-item>
      <a [routerLink]="item.link" class="-mx-1 flex gap-3 rounded-lg px-1 py-2.5 transition hover:bg-surface-850" [class.opacity-60]="state(item) === 'unmonitored'">
        <kr-poster class="w-11 shrink-0 sm:w-12" [url]="item.posterUrl" [title]="item.title" />
        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span class="font-medium text-slate-100">{{ item.title }}</span>
            <span class="badge border-l-2 px-1.5" [class]="styles[styleKey(item)].bar + ' ' + styles[styleKey(item)].chip">{{ item.subtitle }}</span>
            @if (state(item) !== 'upcoming') {
              <span class="badge" [class]="states[state(item)].badge">{{ states[state(item)].label }}</span>
            }
          </div>
          <div class="mt-0.5 text-sm text-slate-400">{{ detail(item) }}</div>
          @if (item.entries[0].overview && item.entries.length === 1) {
            <p class="mt-1 line-clamp-2 text-xs text-slate-500 max-sm:hidden">{{ item.entries[0].overview }}</p>
          }
        </div>
      </a>
    </ng-template>

    <!-- iCal feed -->
    @if (feedOpen()) {
      <kr-modal title="Subscribe to the calendar" (closed)="closeFeed()">
        <div class="space-y-4 text-sm">
          <p class="text-slate-400">Add this URL to Google Calendar, Apple Calendar, Outlook or any app that can subscribe to iCal feeds.</p>
          <div class="grid grid-cols-2 gap-2">
            <label class="flex items-center gap-2"><input type="checkbox" class="accent-accent-500" [(ngModel)]="feed.series" /> Series</label>
            <label class="flex items-center gap-2"><input type="checkbox" class="accent-accent-500" [(ngModel)]="feed.movies" /> Movies</label>
            @if (feed.movies) {
              <label class="flex items-center gap-2"><input type="checkbox" class="accent-accent-500" [(ngModel)]="feed.theatrical" /> In cinemas</label>
              <label class="flex items-center gap-2"><input type="checkbox" class="accent-accent-500" [(ngModel)]="feed.digital" /> Digital</label>
              <label class="flex items-center gap-2"><input type="checkbox" class="accent-accent-500" [(ngModel)]="feed.physical" /> Physical</label>
            }
            <label class="flex items-center gap-2"><input type="checkbox" class="accent-accent-500" [(ngModel)]="feed.unmonitored" /> Unmonitored</label>
          </div>
          <div class="grid grid-cols-2 gap-3">
            <div><label class="label">Days back</label><input type="number" min="0" max="180" class="input" [(ngModel)]="feed.pastDays" /></div>
            <div><label class="label">Days ahead</label><input type="number" min="0" max="220" class="input" [(ngModel)]="feed.futureDays" /></div>
          </div>
          @if (apiKey(); as key) {
            <div>
              <label class="label">Feed URL</label>
              <div class="flex gap-2">
                <input class="input font-mono text-xs sm:text-xs" readonly [value]="feedLink(key)" (focus)="$any($event.target).select()" />
                <button class="btn-secondary shrink-0" (click)="copy(feedLink(key))">Copy</button>
              </div>
              <p class="hint">The URL contains your API key, which gives full access to Krakarr. Only share it with apps you trust; regenerate the key in Settings → General to revoke it.</p>
            </div>
          } @else {
            <p class="text-slate-500">Loading…</p>
          }
        </div>
        <ng-container footer>
          <button class="btn-ghost" (click)="closeFeed()">Close</button>
        </ng-container>
      </kr-modal>
    }
  `,
})
export class CalendarPage {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly styles = STYLES;
  protected readonly states = STATES;
  protected readonly maxChips = 4;
  protected readonly views: { id: CalendarView; label: string }[] = [
    { id: 'month', label: 'Month' }, { id: 'week', label: 'Week' }, { id: 'agenda', label: 'Agenda' },
  ];
  protected readonly statuses: { id: CalendarStatusFilter; label: string }[] = [
    { id: 'all', label: 'Any' }, { id: 'missing', label: 'Missing' }, { id: 'downloaded', label: 'Downloaded' },
  ];
  protected readonly releaseTypes = [
    { key: 'theatrical', style: 'Theatrical' }, { key: 'digital', style: 'Digital' }, { key: 'physical', style: 'Physical' },
  ] as const;

  private readonly stored = readStored();
  private readonly firstDay = localeFirstDayOfWeek();
  protected readonly today = toIsoDate(new Date());

  protected readonly view = signal<CalendarView>(
    this.stored.view ?? (typeof matchMedia !== 'undefined' && matchMedia('(max-width: 639px)').matches ? 'agenda' : 'month'));
  protected readonly anchor = signal(new Date());
  protected readonly filters = signal<CalendarFilters>({ ...DEFAULT_FILTERS, ...this.stored.filters });
  /** Everyone starts on the titles they follow. */
  protected readonly owner = signal<Owner>(this.stored.owner ?? 'me');
  /** Staff: users who follow titles, to view the calendar as them. */
  protected readonly subscribers = signal<Subscriber[]>([]);
  protected readonly entries = signal<CalendarEntry[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selectedDay = signal<string | null>(null);
  protected readonly filtersOpen = signal(false);
  protected readonly feedOpen = signal(false);
  protected readonly apiKey = signal<string | null>(null);
  protected feed: FeedOptions = {
    movies: true, series: true, theatrical: true, digital: true, physical: true, unmonitored: false, pastDays: 14, futureDays: 90, ...this.stored.feed,
  };

  protected readonly range = computed(() => viewRange(this.view(), this.anchor(), this.firstDay));
  protected readonly days = computed(() => daysBetween(this.range().start, this.range().end));
  protected readonly items = computed(() => buildItems(this.entries(), this.filters()));
  protected readonly byDay = computed(() => groupByDay(this.items()));
  protected readonly agendaDays = computed(() => this.days().filter(d => this.byDay().has(d)));
  protected readonly hiddenCount = computed(() => {
    const all = buildItems(this.entries(), { ...DEFAULT_FILTERS, unmonitored: true }).length;
    return Math.max(0, all - this.items().length);
  });
  /** Items per kind in the range, ignoring the kind toggles but honouring status and unmonitored. */
  protected readonly counts = computed(() => {
    const f = this.filters();
    const all = buildItems(this.entries(), { ...DEFAULT_FILTERS, unmonitored: f.unmonitored, status: f.status });
    const counts: Record<StyleKey | 'movies', number> = { episodes: 0, movies: 0, Theatrical: 0, Digital: 0, Physical: 0 };
    for (const item of all) {
      counts[this.styleKey(item)]++;
      if (item.kind === 'movie') counts.movies++;
    }
    return counts;
  });
  protected readonly activeFilterCount = computed(() => {
    const f = this.filters();
    return (Object.keys(DEFAULT_FILTERS) as (keyof CalendarFilters)[])
      .filter(k => f[k] !== DEFAULT_FILTERS[k] && !(f.movies === false && (k === 'theatrical' || k === 'digital' || k === 'physical')))
      .length;
  });
  /** Colours of the kinds currently shown, for the phone summary. */
  protected readonly activeKinds = computed(() => {
    const f = this.filters();
    const kinds: StyleKey[] = f.series ? ['episodes'] : [];
    if (f.movies) for (const t of this.releaseTypes) if (f[t.key]) kinds.push(t.style);
    return kinds;
  });
  protected readonly filterSummary = computed(() => {
    const f = this.filters();
    const types = this.releaseTypes.filter(t => f[t.key]);
    const movies = !f.movies || types.length === 0 ? null
      : types.length === this.releaseTypes.length ? 'Movies' : `Movies (${types.map(t => STYLES[t.style].label.toLowerCase()).join(', ')})`;
    const parts = [f.series ? 'Series' : null, movies].filter(Boolean);
    const what = parts.length ? parts.join(', ') : 'Nothing selected';
    const status = f.status === 'missing' ? ' · missing' : f.status === 'downloaded' ? ' · downloaded' : '';
    return what + status + (f.unmonitored ? ' · incl. unmonitored' : '');
  });
  protected readonly weekdays = computed(() => {
    const start = startOfDisplayedWeek(this.firstDay);
    return Array.from({ length: 7 }, (_, i) => {
      const d = addDays(start, i);
      return { short: d.toLocaleDateString(undefined, { weekday: 'short' }), narrow: d.toLocaleDateString(undefined, { weekday: 'narrow' }) };
    });
  });
  protected readonly periodName = computed(() => (this.view() === 'week' ? 'week' : 'month'));
  protected readonly rangeLabel = computed(() => {
    const { start, end } = this.range();
    if (this.view() !== 'week') return this.anchor().toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
    const fmt = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    return fmt.formatRange ? fmt.formatRange(start, end) : `${fmt.format(start)} – ${fmt.format(end)}`;
  });

  private requestId = 0;
  private scrollToToday = true;

  constructor() {
    effect(() => { void this.load(this.range(), false, this.owner()); });
    if (this.auth.isStaff()) {
      this.api.subscribers().then(users => {
        this.subscribers.set(users);
        // A stored user who no longer follows anything (or was removed) falls back to your own titles.
        const owner = this.owner();
        if (typeof owner === 'number' && !users.some(u => u.id === owner)) this.owner.set('me');
      }).catch(() => { /* picker keeps Everything / My titles */ });
    }
    effect(() => this.persist());
    merge(this.events.movieUpdated, this.events.seriesUpdated, this.events.historyAdded)
      .pipe(debounceTime(1500), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => void this.load(this.range(), true, this.owner()));
  }

  private async load(range: { start: Date; end: Date }, quiet: boolean, owner: Owner) {
    const id = ++this.requestId;
    if (!quiet) this.loading.set(true);
    try {
      const entries = await this.api.calendar(toIsoDate(range.start), toIsoDate(range.end), owner === 'all' ? null : owner);
      if (id !== this.requestId) return;
      this.entries.set(entries);
      this.error.set(null);
      if (this.scrollToToday && this.view() === 'agenda') {
        this.scrollToToday = false;
        setTimeout(() => this.scrollToAgendaDay());
      }
    } catch (e) {
      if (id === this.requestId) this.error.set(errorMessage(e));
    } finally {
      if (id === this.requestId) this.loading.set(false);
    }
  }

  private scrollToAgendaDay() {
    const target = this.agendaDays().find(d => d >= this.today);
    if (target && target !== this.agendaDays()[0]) {
      document.getElementById('day-' + target)?.scrollIntoView({ block: 'start' });
    }
  }

  private persist() {
    const value: Stored = { view: this.view(), filters: this.filters(), feed: this.feed, owner: this.owner() };
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(value)); } catch { /* storage unavailable */ }
  }

  protected setView(view: CalendarView) {
    this.view.set(view);
    this.selectedDay.set(null);
    this.scrollToToday = view === 'agenda';
  }

  protected move(direction: 1 | -1) {
    this.anchor.set(shiftAnchor(this.view(), this.anchor(), direction));
    this.selectedDay.set(null);
  }

  protected goToday() {
    this.anchor.set(new Date());
    this.selectedDay.set(this.view() === 'month' ? this.today : null);
    this.scrollToToday = true;
    if (this.view() === 'agenda') setTimeout(() => this.scrollToAgendaDay());
  }

  protected selectDay(day: string) { this.selectedDay.set(this.selectedDay() === day ? null : day); }

  protected toggle(key: 'movies' | 'series' | 'theatrical' | 'digital' | 'physical' | 'unmonitored') {
    this.filters.update(f => ({ ...f, [key]: !f[key] }));
  }

  protected isOn(key: keyof CalendarFilters) { return this.filters()[key] === true; }

  protected resetFilters() { this.filters.set({ ...DEFAULT_FILTERS }); }

  protected setFilter<K extends keyof CalendarFilters>(key: K, value: CalendarFilters[K]) {
    this.filters.update(f => ({ ...f, [key]: value }));
  }

  protected chip(active: boolean) {
    return 'inline-flex shrink-0 items-center gap-1.5 rounded-full border px-3 py-1 text-xs font-medium transition pointer-coarse:min-h-9 '
      + (active ? 'border-surface-600 bg-surface-700 text-white' : 'border-surface-700 text-slate-500 hover:text-slate-300');
  }

  protected inMonth(day: string) { return parseIsoDate(day).getMonth() === this.anchor().getMonth(); }
  protected dayNumber(day: string) { return parseIsoDate(day).getDate(); }
  protected weekdayName(day: string) { return parseIsoDate(day).toLocaleDateString(undefined, { weekday: 'short' }); }
  protected longDate(day: string) {
    return parseIsoDate(day).toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
  }
  private shortDate(day: string) { return parseIsoDate(day).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }); }

  protected styleKey(item: CalendarItem): StyleKey { return item.kind === 'episodes' ? 'episodes' : item.releaseType ?? 'Theatrical'; }
  protected state(item: CalendarItem) { return itemState(item, this.today); }
  protected stateText(item: CalendarItem) {
    const s = this.state(item);
    return s === 'downloaded' ? 'text-emerald-300' : s === 'downloading' ? 'text-accent-300' : 'text-rose-300';
  }

  /** Secondary line: episode title(s) and network, or where a movie date comes from. */
  protected detail(item: CalendarItem): string {
    const e = item.entries[0];
    if (item.kind === 'episodes') {
      const what = item.entries.length === 1 ? (e.episodeTitle || 'TBA') : `${item.entries.length} episodes`;
      return e.network ? `${what} · ${e.network}` : what;
    }
    const where = e.country ? (e.isRegionDate ? e.country : `${e.country}, earliest`) : '';
    const earlier = e.earliestDate ? ` · ${e.earliestCountry} ${this.shortDate(e.earliestDate)}` : '';
    return [e.year ? String(e.year) : '', where].filter(Boolean).join(' · ') + earlier;
  }

  protected tooltip(item: CalendarItem) {
    return `${item.title} — ${item.subtitle}\n${this.detail(item)}\n${STATES[this.state(item)].label}`;
  }

  // ---- iCal ----
  async openFeed() {
    this.feedOpen.set(true);
    if (!this.apiKey()) {
      try { this.apiKey.set((await this.api.settings('general')).apiKey); } catch (e) { this.toasts.error(errorMessage(e)); }
    }
  }

  protected feedLink(key: string) {
    const owner = this.owner();
    return feedUrl(document.baseURI, key, this.feed, typeof owner === 'number' ? owner : undefined);
  }

  protected closeFeed() {
    this.feedOpen.set(false);
    this.persist();
  }

  protected async copy(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      this.toasts.success('Feed URL copied');
    } catch {
      this.toasts.error('Copy failed — select the URL and copy it manually');
    }
  }
}

function startOfDisplayedWeek(firstDay: number): Date {
  const d = new Date(2024, 0, 7); // a Sunday
  return addDays(d, firstDay);
}
