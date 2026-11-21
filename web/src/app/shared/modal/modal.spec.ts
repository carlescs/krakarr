import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, settle, text } from '../../../testing/test-setup';
import { Modal } from './modal';

@Component({
  imports: [Modal],
  template: `@if (open()) { <kr-modal title="Edit" (closed)="closed = closed + 1"><p>Body</p><button footer>Save</button></kr-modal> }`,
})
class Host {
  readonly open = signal(true);
  closed = 0;
}

describe('Modal', () => {
  it('projects the body and footer and locks page scrolling while open', async () => {
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h2')).toBe('Edit');
    expect(text(el, '[role="dialog"]')).toContain('Body');
    expect(button(el, 'Save')).toBeTruthy();
    expect(document.body.style.overflow).toBe('hidden');

    fixture.componentInstance.open.set(false);
    await settle(fixture);
    expect(document.body.style.overflow).toBe('');
  });

  it('closes from the close button, the backdrop and Escape, but not from clicks inside', async () => {
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Close').click();
    (el.querySelector('[role="dialog"]') as HTMLElement).click();
    expect(fixture.componentInstance.closed).toBe(1);
    (el.querySelector('.fixed') as HTMLElement).click();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(fixture.componentInstance.closed).toBe(3);
    fixture.destroy();
  });
});
