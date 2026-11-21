import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Toasts } from '../../core/toast';
import { button, settle, setupTest, text } from '../../../testing/test-setup';
import { ToastHost } from './toast-host';

describe('ToastHost', () => {
  it('shows toasts and dismisses them', async () => {
    setupTest();
    const toasts = TestBed.inject(Toasts);
    const fixture = TestBed.createComponent(ToastHost);
    toasts.error('Something broke');
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Something broke');

    button(fixture.nativeElement, 'Dismiss').click();
    await settle(fixture);
    expect(toasts.items()).toEqual([]);
    expect(text(fixture.nativeElement)).not.toContain('Something broke');
  });
});
