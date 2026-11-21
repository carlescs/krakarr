import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { StateBadge } from './state-badge';

describe('StateBadge', () => {
  it('labels and colours the download state', async () => {
    const fixture = TestBed.createComponent(StateBadge);
    fixture.componentRef.setInput('state', 'FetchingMetadata');
    await fixture.whenStable();
    const badge = fixture.nativeElement.querySelector('span') as HTMLElement;
    expect(badge.textContent).toBe('Metadata');
    expect(badge.className).toContain('text-violet-300');

    fixture.componentRef.setInput('state', 'Error');
    await fixture.whenStable();
    expect(badge.textContent).toBe('Error');
    expect(badge.className).toContain('text-rose-300');
  });
});
