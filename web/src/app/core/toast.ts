import { Component, Injectable, inject, signal } from '@angular/core';

export interface Toast { id: number; kind: 'success' | 'error' | 'info'; message: string; }

@Injectable({ providedIn: 'root' })
export class Toasts {
  readonly items = signal<Toast[]>([]);
  private nextId = 1;

  show(message: string, kind: Toast['kind'] = 'info', timeout = 4000) {
    const toast = { id: this.nextId++, kind, message };
    this.items.update(t => [...t, toast]);
    setTimeout(() => this.dismiss(toast.id), timeout);
  }
  success(message: string) { this.show(message, 'success'); }
  error(message: string) { this.show(message, 'error', 7000); }
  dismiss(id: number) { this.items.update(t => t.filter(x => x.id !== id)); }
}

@Component({
  selector: 'kr-toasts',
  template: `
    <div class="pointer-events-none fixed bottom-4 right-4 z-50 flex w-80 flex-col gap-2">
      @for (t of toasts.items(); track t.id) {
        <div class="pointer-events-auto flex items-start gap-3 rounded-lg border px-4 py-3 text-sm shadow-lg backdrop-blur"
             [class]="t.kind === 'error' ? 'border-rose-500/40 bg-rose-950/90 text-rose-100'
                    : t.kind === 'success' ? 'border-emerald-500/40 bg-emerald-950/90 text-emerald-100'
                    : 'border-surface-600 bg-surface-800/95 text-slate-100'">
          <span class="flex-1">{{ t.message }}</span>
          <button class="text-slate-400 hover:text-white" (click)="toasts.dismiss(t.id)" aria-label="Dismiss">✕</button>
        </div>
      }
    </div>
  `,
})
export class ToastHost {
  protected readonly toasts = inject(Toasts);
}
