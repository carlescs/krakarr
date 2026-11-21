import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { MockApi, button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubscribeButton } from './subscribe-button';

async function render(api: MockApi) {
  setupTest({ api });
  const fixture = TestBed.createComponent(SubscribeButton);
  fixture.componentRef.setInput('mediaType', 'Movie');
  fixture.componentRef.setInput('libraryId', 3);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

describe('SubscribeButton', () => {
  it('follows a title', async () => {
    const api = mockApi({ subscriptionState: async () => ({ subscribed: false }), subscribe: async () => undefined });
    const { fixture, el } = await render(api);
    expect(api.subscriptionState).toHaveBeenCalledWith('Movie', 3);
    const toggle = button(el, 'Follow');
    expect(toggle.className).toContain('btn-primary');

    toggle.click();
    await settle(fixture);
    expect(api.subscribe).toHaveBeenCalledWith('Movie', 3);
    expect(text(el, 'button')).toBe('Following');
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
  });

  it('unfollows and shows staff who follows it', async () => {
    let followers = ['kim', 'tester', 'lee', 'sam'];
    const api = mockApi({
      subscriptionState: async () => ({ subscribed: followers.includes('tester'), subscribers: followers }),
      unsubscribe: async () => { followers = followers.filter(f => f !== 'tester'); },
    });
    const { fixture, el } = await render(api);
    expect(text(el)).toContain('Followed by kim, tester, lee +1');

    button(el, 'Following').click();
    await settle(fixture);
    expect(api.unsubscribe).toHaveBeenCalledWith('Movie', 3);
    expect(text(el)).toContain('Followed by kim, lee, sam');
    expect(text(el, 'button')).toBe('Follow');
  });
});
