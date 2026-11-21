import { Injectable, signal } from '@angular/core';

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
