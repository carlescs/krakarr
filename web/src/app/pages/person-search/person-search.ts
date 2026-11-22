import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { PersonLookup } from '../../core/models';

@Component({
  selector: 'kr-person-search',
  imports: [FormsModule, RouterLink],
  templateUrl: './person-search.html',
  styleUrl: './person-search.scss',
})
export class PersonSearchPage {
  private readonly api = inject(Api);

  protected term = '';
  protected readonly searching = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<PersonLookup[]>([]);

  protected initials(name: string) {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0]).join('').toUpperCase();
  }

  async search() {
    if (!this.term.trim()) return;
    this.searching.set(true);
    this.error.set(null);
    try {
      this.results.set(await this.api.lookupPeople(this.term.trim()));
      if (this.results().length === 0) this.error.set('No people found.');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }
}
