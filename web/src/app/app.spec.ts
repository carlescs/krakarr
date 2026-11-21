import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Toasts } from './core/toast';
import { settle, setupTest, text } from '../testing/test-setup';
import { App } from './app';

describe('App', () => {
  it('hosts the router outlet and the toasts', async () => {
    setupTest();
    const fixture = TestBed.createComponent(App);
    TestBed.inject(Toasts).success('Saved');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('router-outlet')).not.toBeNull();
    expect(text(el, 'kr-toasts')).toContain('Saved');
  });
});
