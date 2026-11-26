import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { AlternateTitle } from '../../core/models';
import { AlternateTitles } from './alternate-titles';

const titles: AlternateTitle[] = [
  { id: 1, title: 'El viaje de Chihiro', language: 'es', source: 'Metadata' },
  { id: 2, title: 'Sen to Chihiro', language: null, source: 'Scene' },
  { id: 3, title: 'Chihiro', language: 'es', source: 'User' },
];

async function create(setAlternateTitles = vi.fn(async () => titles)) {
  setupTest({
    api: mockApi({
      languages: async () => ({ all: [{ code: 'en', name: 'English' }, { code: 'es', name: 'Spanish' }], preferred: [] }),
      alternateTitles: async () => titles,
      setAlternateTitles,
    }),
  });
  const fixture = TestBed.createComponent(AlternateTitles);
  fixture.componentRef.setInput('kind', 'movies');
  fixture.componentRef.setInput('itemId', 7);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement, setAlternateTitles };
}

describe('AlternateTitles', () => {
  it('shows metadata and scene titles read-only and the user titles as editable rows', async () => {
    const { el } = await create();
    expect(text(el)).toContain('El viaje de Chihiro · Spanish');
    expect(text(el)).toContain('Sen to Chihiro · scene');
    const inputs = el.querySelectorAll<HTMLInputElement>('input[aria-label="Alternate title"]');
    expect(inputs.length).toBe(1);
    expect(inputs[0].value).toBe('Chihiro');
  });

  it('saves only when the user titles changed, dropping blank rows', async () => {
    const { fixture, el, setAlternateTitles } = await create();
    await fixture.componentInstance.save();
    expect(setAlternateTitles).not.toHaveBeenCalled();

    (el.querySelector('button.btn-secondary') as HTMLButtonElement).click();
    await settle(fixture);
    await fixture.componentInstance.save();
    expect(setAlternateTitles).not.toHaveBeenCalled(); // the new row is blank

    const inputs = el.querySelectorAll<HTMLInputElement>('input[aria-label="Alternate title"]');
    inputs[1].value = '  La Oficina ';
    inputs[1].dispatchEvent(new Event('input'));
    await settle(fixture);
    await fixture.componentInstance.save();
    expect(setAlternateTitles).toHaveBeenCalledWith('movies', 7, [
      { title: 'Chihiro', language: 'es' },
      { title: 'La Oficina', language: null },
    ]);
  });
});
