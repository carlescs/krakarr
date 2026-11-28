import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { CustomFormat, CustomFormatSchema } from '../../../core/models';
import { CustomFormats } from './custom-formats';

const atmos: CustomFormat = {
  id: 1, name: 'Atmos',
  specifications: [{ name: '', condition: 'Audio', value: 'Atmos', min: 0, max: 0, exceptLanguage: false, negate: false, required: true }],
};
const schema: CustomFormatSchema[] = [
  { condition: 'ReleaseTitle', values: [] },
  { condition: 'Audio', values: ['Atmos', 'TrueHD'] },
  { condition: 'Resolution', values: ['720', '1080', '2160'] },
];

describe('CustomFormats', () => {
  it('lists formats with their conditions', async () => {
    setupTest({ api: mockApi({ customFormats: async () => [atmos], customFormatSchema: async () => schema }) });
    const fixture = TestBed.createComponent(CustomFormats);
    await settle(fixture);
    expect(text(fixture.nativeElement, '[data-formats]')).toContain('Atmos');
    expect(text(fixture.nativeElement, '[data-formats]')).toContain('Audio is Atmos (required)');
  });

  it('creates a format and reports the new list', async () => {
    const api = setupTest({
      api: mockApi({ customFormats: async () => [], customFormatSchema: async () => schema, saveCustomFormat: async () => atmos }),
    });
    const fixture = TestBed.createComponent(CustomFormats);
    let emitted: CustomFormat[] | undefined;
    fixture.componentInstance.changed.subscribe(f => (emitted = f));
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-empty]')).not.toBeNull();

    button(el, '+ Add format').click();
    await settle(fixture);
    type(el, '#cf-name', ' Remux 4K ');
    const condition = el.querySelector<HTMLSelectElement>('[data-spec] select[aria-label="Condition"]')!;
    condition.value = condition.options[[...condition.options].findIndex(o => o.textContent?.trim() === 'Resolution')].value;
    condition.dispatchEvent(new Event('change'));
    await settle(fixture);
    // A new kind of condition starts from its first allowed value.
    expect(el.querySelector<HTMLSelectElement>('[data-spec] select[aria-label="Value"]')!.selectedOptions[0].textContent?.trim()).toBe('720');

    api.customFormats.mockImplementation(async () => [atmos]);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveCustomFormat).toHaveBeenCalledWith(expect.objectContaining({
      id: 0, name: 'Remux 4K', specifications: [expect.objectContaining({ condition: 'Resolution', value: '720' })],
    }));
    expect(emitted).toEqual([atmos]);
  });

  it('imports pasted JSON and shows warnings', async () => {
    const api = setupTest({
      api: mockApi({
        customFormats: async () => [], customFormatSchema: async () => schema,
        importCustomFormats: async () => ({ created: 2, updated: 0, warnings: ['Skipped "Freeleech": uses IndexerFlagSpecification'] }),
      }),
    });
    const fixture = TestBed.createComponent(CustomFormats);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Import').click();
    await settle(fixture);
    type(el, '#cf-import-json', '[{"name":"x"}]');
    type(el, '#cf-import-app', 'Sonarr');
    await settle(fixture);
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Import').click();
    await settle(fixture);
    expect(api.importCustomFormats).toHaveBeenCalledWith('[{"name":"x"}]', 'Sonarr');
    expect(text(el, '[data-import-result]')).toContain('Created 2, updated 0.');
    expect(text(el, '[data-import-result]')).toContain('Freeleech');
  });

  it('tests a release name', async () => {
    const api = setupTest({
      api: mockApi({
        customFormats: async () => [atmos], customFormatSchema: async () => schema,
        testCustomFormats: async () => ({
          quality: 'Bluray-2160p', releaseGroup: 'GRP', hdr: ['DV'], videoCodec: 'x265', audio: ['Atmos', 'TrueHD'], audioChannels: '7.1',
          editions: [], streamingService: null, languages: [], flags: [], formats: [{ id: 1, name: 'Atmos' }],
        }),
      }),
    });
    const fixture = TestBed.createComponent(CustomFormats);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, '#cf-test', 'Movie.2020.2160p.BluRay.TrueHD.7.1.Atmos.DV.x265-GRP');
    button(el, 'Test').click();
    await settle(fixture);
    expect(api.testCustomFormats).toHaveBeenCalledWith('Movie.2020.2160p.BluRay.TrueHD.7.1.Atmos.DV.x265-GRP');
    expect(text(el, '[data-test-result]')).toContain('Atmos, TrueHD 7.1');
    expect(text(el, '[data-matched]')).toBe('Atmos');
  });
});
