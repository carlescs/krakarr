import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import {
  CustomFormat, CustomFormatApp, CustomFormatCondition, CustomFormatImportResult, CustomFormatSchema, CustomFormatSpecification, CustomFormatTestResult,
} from '../../../core/models';
import { Toasts } from '../../../core/toast';
import { Modal } from '../../../shared/modal/modal';

/** How each condition is labelled in the editor, and what its value means. */
export const CONDITION_INFO: Record<CustomFormatCondition, { label: string; hint: string }> = {
  ReleaseTitle: { label: 'Release title', hint: 'Regular expression matched against the release name (case-insensitive).' },
  ReleaseGroup: { label: 'Release group', hint: 'Regular expression matched against the release group.' },
  Edition: { label: 'Edition', hint: 'Regular expression matched against the editions in the name (Director\'s Cut, IMAX, Extended…).' },
  Language: { label: 'Language', hint: 'An audio language of the release.' },
  Source: { label: 'Source', hint: 'Where the release comes from. Bluray includes remuxes.' },
  Resolution: { label: 'Resolution', hint: 'Video resolution.' },
  QualityModifier: { label: 'Quality modifier', hint: 'Remux releases.' },
  Size: { label: 'Size', hint: 'Release size in GB (0 = no limit).' },
  Indexer: { label: 'Indexer', hint: 'Regular expression matched against the indexer name.' },
  Hdr: { label: 'HDR', hint: 'Dynamic range. "HDR" is any HDR format; "DV-only" is Dolby Vision without an HDR10 fallback.' },
  VideoCodec: { label: 'Video codec', hint: 'Video codec tagged in the name.' },
  Audio: { label: 'Audio', hint: 'Audio format or channel layout tagged in the name.' },
  StreamingService: { label: 'Streaming service', hint: 'Service a WEB release was taken from.' },
  ReleaseType: { label: 'Release type', hint: 'Single episode, multi-episode or season pack (series).' },
  Flag: { label: 'Flag', hint: '3D, hardcoded subtitles or upscaled releases.' },
};

const blankSpec = (condition: CustomFormatCondition = 'ReleaseTitle', value = ''): CustomFormatSpecification =>
  ({ name: '', condition, value, min: 0, max: 0, exceptLanguage: false, negate: false, required: false });

/**
 * Custom formats (Radarr/Sonarr style): named sets of conditions that quality profiles score. Supports pasting TRaSH Guides /
 * Radarr / Sonarr JSON, exporting, and testing a release name against every format.
 */
@Component({
  selector: 'kr-custom-formats',
  imports: [FormsModule, Modal],
  templateUrl: './custom-formats.html',
  styleUrl: './custom-formats.scss',
})
export class CustomFormats implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  /** The formats after every change, so quality profiles can offer them. */
  readonly changed = output<CustomFormat[]>();

  protected readonly info = CONDITION_INFO;
  protected readonly conditions = Object.keys(CONDITION_INFO) as CustomFormatCondition[];
  protected readonly formats = signal<CustomFormat[]>([]);
  protected readonly schema = signal<CustomFormatSchema[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly form = signal<CustomFormat | null>(null);
  protected readonly confirmDelete = signal<CustomFormat | null>(null);

  protected readonly importOpen = signal(false);
  protected importJson = '';
  protected importApp: CustomFormatApp = 'Radarr';
  protected readonly importResult = signal<CustomFormatImportResult | null>(null);

  protected readonly exportJson = signal<string | null>(null);
  protected exportApp: CustomFormatApp = 'Radarr';

  protected testTitle = '';
  protected readonly testResult = signal<CustomFormatTestResult | null>(null);

  private readonly valuesByCondition = computed(() => new Map(this.schema().map(s => [s.condition, s.values])));

  async ngOnInit() {
    try {
      const [formats, schema] = await Promise.all([this.api.customFormats(), this.api.customFormatSchema()]);
      this.formats.set(formats);
      this.schema.set(schema);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected valuesFor(condition: CustomFormatCondition): string[] { return this.valuesByCondition().get(condition) ?? []; }

  protected isRegex(condition: CustomFormatCondition) {
    return condition === 'ReleaseTitle' || condition === 'ReleaseGroup' || condition === 'Edition' || condition === 'Indexer';
  }

  /** "Audio is Atmos", "not Resolution is 2160", "Size 1–10 GB". */
  protected describe(s: CustomFormatSpecification): string {
    const label = this.info[s.condition].label;
    const what = s.condition === 'Size' ? `${label} ${s.min || 0}–${s.max || '∞'} GB`
      : this.isRegex(s.condition) ? `${label} ~ ${s.value}`
      : s.condition === 'Language' && s.exceptLanguage ? `${label} other than ${s.value}`
      : `${label} is ${s.value}`;
    return (s.negate ? 'not ' : '') + what + (s.required ? ' (required)' : '');
  }

  edit(f: CustomFormat | null) {
    this.form.set(f ? structuredClone(f) : { id: 0, name: '', specifications: [blankSpec()] });
  }

  addSpec(f: CustomFormat) {
    f.specifications = [...f.specifications, blankSpec()];
    this.form.set({ ...f });
  }

  removeSpec(f: CustomFormat, index: number) {
    f.specifications = f.specifications.filter((_, i) => i !== index);
    this.form.set({ ...f });
  }

  /** A new condition kind starts from its first allowed value (or empty for regexes and sizes). */
  conditionChanged(f: CustomFormat, s: CustomFormatSpecification) {
    s.value = this.valuesFor(s.condition)[0] ?? '';
    s.min = 0;
    s.max = 0;
    s.exceptLanguage = false;
    this.form.set({ ...f });
  }

  async save(f: CustomFormat) {
    this.saving.set(true);
    try {
      await this.api.saveCustomFormat({ ...f, name: f.name.trim() });
      this.form.set(null);
      await this.reload();
      this.toasts.success('Custom format saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  async remove(f: CustomFormat) {
    try {
      await this.api.deleteCustomFormat(f.id);
      this.confirmDelete.set(null);
      this.form.set(null);
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  openImport() {
    this.importJson = '';
    this.importResult.set(null);
    this.importOpen.set(true);
  }

  async runImport() {
    this.saving.set(true);
    try {
      const result = await this.api.importCustomFormats(this.importJson, this.importApp);
      this.importResult.set(result);
      await this.reload();
      if (result.warnings.length === 0) {
        this.importOpen.set(false);
        this.toasts.success(`Imported ${result.created + result.updated} custom format(s)`);
      }
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  async openExport() {
    try {
      this.exportJson.set(await this.api.exportCustomFormats(this.exportApp));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async copyExport() {
    try {
      await navigator.clipboard.writeText(this.exportJson() ?? '');
      this.toasts.success('Copied to the clipboard');
    } catch {
      this.toasts.error('Could not copy; select the text and copy it instead');
    }
  }

  protected matchedNames(r: CustomFormatTestResult) { return r.formats.length ? r.formats.map(f => f.name).join(', ') : 'None'; }

  async test() {
    if (!this.testTitle.trim()) return;
    try {
      this.testResult.set(await this.api.testCustomFormats(this.testTitle.trim()));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  private async reload() {
    const formats = await this.api.customFormats();
    this.formats.set(formats);
    this.changed.emit(formats);
  }
}
