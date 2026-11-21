import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { LanguageOverride } from './language-override';

describe('LanguageOverride', () => {
  it('follows the default until unticked, then starts from the default list', async () => {
    setupTest({
      api: mockApi({ languages: async () => ({ all: [{ code: 'en', name: 'English' }, { code: 'es', name: 'Spanish' }], preferred: ['es'] }) }),
    });
    const fixture = TestBed.createComponent(LanguageOverride);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Use the default (Spanish)');
    expect(el.querySelector('kr-language-picker')).toBeNull();

    const checkbox = el.querySelector('input[type="checkbox"]') as HTMLInputElement;
    checkbox.checked = false;
    checkbox.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(fixture.componentInstance.value()).toEqual(['es']);
    expect(el.querySelector('kr-language-picker')).not.toBeNull();
  });
});
