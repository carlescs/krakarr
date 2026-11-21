import { Component, inject } from '@angular/core';
import { Toasts } from '../../core/toast';

@Component({
  selector: 'kr-toasts',
  templateUrl: './toast-host.html',
  styleUrl: './toast-host.scss',
})
export class ToastHost {
  protected readonly toasts = inject(Toasts);
}
