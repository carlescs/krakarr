import { Component, model } from '@angular/core';
import { ViewMode } from '../../core/selection';

/** Grid / table switch for the library pages. */
@Component({
  selector: 'kr-view-toggle',
  host: { class: 'inline-flex shrink-0 rounded-lg bg-surface-800 p-0.5', role: 'group', 'aria-label': 'View' },
  templateUrl: './view-toggle.html',
  styleUrl: './view-toggle.scss',
})
export class ViewToggle {
  readonly mode = model.required<ViewMode>();
}
