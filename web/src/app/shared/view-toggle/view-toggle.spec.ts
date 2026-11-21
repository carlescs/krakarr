import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ViewMode } from '../../core/selection';
import { button, settle, setupTest } from '../../../testing/test-setup';
import { ViewToggle } from './view-toggle';

@Component({ imports: [ViewToggle], template: '<kr-view-toggle [(mode)]="mode" />' })
class Host { readonly mode = signal<ViewMode>('grid'); }

describe('ViewToggle', () => {
  it('switches between grid and table', async () => {
    setupTest();
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(button(el, 'Poster grid').getAttribute('aria-pressed')).toBe('true');

    button(el, 'Table').click();
    await settle(fixture);
    expect(fixture.componentInstance.mode()).toBe('table');
    expect(button(el, 'Table').getAttribute('aria-pressed')).toBe('true');
  });
});
