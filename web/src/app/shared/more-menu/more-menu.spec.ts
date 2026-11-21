import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, settle } from '../../../testing/test-setup';
import { MoreMenu } from './more-menu';

@Component({
  imports: [MoreMenu],
  template: `<kr-more-menu label="Movie actions"><button class="menu-item">Delete</button></kr-more-menu><p id="outside">x</p>`,
})
class Host {}

describe('MoreMenu', () => {
  it('opens on click and closes from an item, outside clicks and Escape', async () => {
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const toggle = button(el, 'Movie actions');
    const menu = () => el.querySelector('[role="menu"]');
    expect(menu()).toBeNull();

    toggle.click();
    await settle(fixture);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    button(el, 'Delete').click();
    await settle(fixture);
    expect(menu()).toBeNull();

    toggle.click();
    await settle(fixture);
    (el.querySelector('#outside') as HTMLElement).click();
    await settle(fixture);
    expect(menu()).toBeNull();

    toggle.click();
    await settle(fixture);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle(fixture);
    expect(menu()).toBeNull();
  });
});
