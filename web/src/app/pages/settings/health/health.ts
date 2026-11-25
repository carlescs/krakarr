import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Events } from '../../../core/events';
import { HealthIssue, HealthReport } from '../../../core/models';
import { Toasts } from '../../../core/toast';

/**
 * Admin-only list of setup problems found by the server's health checks (disk space, root folders, download client,
 * indexers, API keys). The checks run every few minutes and after settings change; the page follows each run live.
 */
@Component({
  selector: 'kr-health-settings',
  imports: [DatePipe, RouterLink],
  templateUrl: './health.html',
  styleUrl: './health.scss',
})
export class HealthSettings implements OnInit {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly toasts = inject(Toasts);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly report = signal<HealthReport | null>(null);
  protected readonly checking = signal(false);
  protected readonly errors = computed(() => this.report()?.issues.filter(i => i.severity === 'Error').length ?? 0);
  protected readonly warnings = computed(() => this.report()?.issues.filter(i => i.severity === 'Warning').length ?? 0);

  ngOnInit() {
    this.events.healthUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
    this.load();
  }

  private async load() {
    try {
      this.report.set(await this.api.health());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async checkNow() {
    this.checking.set(true);
    try {
      this.report.set(await this.api.checkHealth());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.checking.set(false);
    }
  }

  protected tone(issue: HealthIssue) {
    return issue.severity === 'Error' ? 'bg-rose-500/15 text-rose-300' : 'bg-amber-500/15 text-amber-300';
  }
}
