import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { Languages } from '../../core/languages';
import { AlternateTitle, AlternateTitleInput } from '../../core/models';

/**
 * Lists a movie's or series' alternate titles (from the metadata source and TheXEM, read-only) and edits the user's own.
 * The parent calls `save()` when its edit dialog is saved.
 */
@Component({
  selector: 'kr-alternate-titles',
  imports: [FormsModule],
  templateUrl: './alternate-titles.html',
  styleUrl: './alternate-titles.scss',
})
export class AlternateTitles implements OnInit {
  private readonly api = inject(Api);
  protected readonly languages = inject(Languages);

  readonly kind = input.required<'movies' | 'series'>();
  readonly itemId = input.required<number>();

  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly others = signal<AlternateTitle[]>([]);
  protected readonly rows = signal<AlternateTitleInput[]>([]);
  protected readonly showAll = signal(false);
  /** Read-only titles shown before "Show all". */
  protected readonly visibleOthers = computed(() => this.showAll() ? this.others() : this.others().slice(0, 8));
  private initial = '[]';

  async ngOnInit() {
    this.languages.load().catch(() => {});
    try {
      const titles = await this.api.alternateTitles(this.kind(), this.itemId());
      this.apply(titles);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loaded.set(true);
    }
  }

  protected add() { this.rows.update(r => [...r, { title: '', language: null }]); }
  protected remove(index: number) { this.rows.update(r => r.filter((_, i) => i !== index)); }
  protected setTitle(index: number, title: string) { this.rows.update(r => r.map((row, i) => i === index ? { ...row, title } : row)); }
  protected setLanguage(index: number, language: string) {
    this.rows.update(r => r.map((row, i) => i === index ? { ...row, language: language || null } : row));
  }

  protected sourceLabel(t: AlternateTitle) { return t.source === 'Scene' ? 'scene' : null; }

  /** Saves the user's titles when they changed. Throws when the server rejects them. */
  async save(): Promise<void> {
    if (!this.loaded() || this.error()) return;
    const titles = this.cleaned();
    if (JSON.stringify(titles) === this.initial) return;
    this.apply(await this.api.setAlternateTitles(this.kind(), this.itemId(), titles));
  }

  private cleaned(): AlternateTitleInput[] {
    return this.rows().map(r => ({ title: r.title.trim(), language: r.language })).filter(r => r.title.length > 0);
  }

  private apply(titles: AlternateTitle[]) {
    this.others.set(titles.filter(t => t.source !== 'User'));
    const mine = titles.filter(t => t.source === 'User').map(t => ({ title: t.title, language: t.language }));
    this.rows.set(mine);
    this.initial = JSON.stringify(mine);
  }
}
