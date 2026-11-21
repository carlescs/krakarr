import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Languages } from '../../core/languages';
import { button, settle, setupTest, text } from '../../../testing/test-setup';
import { LanguagePicker } from './language-picker';

const languages = [{ code: 'en', name: 'English' }, { code: 'es', name: 'Spanish' }, { code: 'fr', name: 'French' }];

async function render(value: string[]) {
  setupTest();
  TestBed.inject(Languages).all.set(languages);
  const fixture = TestBed.createComponent(LanguagePicker);
  fixture.componentRef.setInput('languages', languages);
  fixture.componentRef.setInput('value', value);
  await settle(fixture);
  return fixture;
}

describe('LanguagePicker', () => {
  it('lists the chosen languages in order and offers the others', async () => {
    const fixture = await render(['es', 'en']);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('1. Spanish');
    expect(text(el)).toContain('2. English');
    expect([...el.querySelectorAll('option')].map(o => o.textContent)).toEqual(['Add language…', 'French']);
  });

  it('adds, reorders and removes languages', async () => {
    const fixture = await render(['es']);
    const el = fixture.nativeElement as HTMLElement;
    const select = el.querySelector('select') as HTMLSelectElement;
    select.value = 'fr';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(fixture.componentInstance.value()).toEqual(['es', 'fr']);

    button(el, 'Prefer French more').click();
    await settle(fixture);
    expect(fixture.componentInstance.value()).toEqual(['fr', 'es']);

    button(el, 'Remove Spanish').click();
    await settle(fixture);
    expect(fixture.componentInstance.value()).toEqual(['fr']);
  });
});
