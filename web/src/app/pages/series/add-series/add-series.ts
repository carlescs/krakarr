import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { SeriesLookup } from '../../../core/models';
import { AddSeriesModal } from '../../../shared/add-series-modal/add-series-modal';
import { Poster } from '../../../shared/poster/poster';

@Component({
  selector: 'kr-add-series',
  imports: [FormsModule, RouterLink, Poster, AddSeriesModal],
  templateUrl: './add-series.html',
  styleUrl: './add-series.scss',
})
export class AddSeriesPage {
  private readonly api = inject(Api);
  protected readonly router = inject(Router);

  protected term = '';
  protected readonly searching = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<SeriesLookup[]>([]);
  protected readonly selected = signal<SeriesLookup | null>(null);

  async search() {
    if (!this.term.trim()) return;
    this.searching.set(true);
    this.error.set(null);
    try {
      this.results.set(await this.api.lookupSeries(this.term.trim()));
      if (this.results().length === 0) this.error.set('No series found.');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }
}
