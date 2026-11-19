import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ToastHost } from './core/toast';

@Component({
  selector: 'kr-root',
  imports: [RouterOutlet, ToastHost],
  template: `<router-outlet /><kr-toasts />`,
})
export class App {}
