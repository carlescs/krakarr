import { EnvironmentProviders, Provider } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Mock, vi } from 'vitest';
import { Api } from '../app/core/api';
import { Auth } from '../app/core/auth';
import { UserRole } from '../app/core/models';

/** An `Api` whose methods are all `vi.fn()` mocks; the ones a test doesn't set never resolve, leaving the component loading. */
export type MockApi = { [K in keyof Api]: Api[K] extends (...args: infer A) => infer R ? Mock<(...args: A) => R> : Api[K] };

export function mockApi(overrides: Record<string, (...args: never[]) => unknown> = {}): MockApi {
  const fns = new Map<PropertyKey, Mock>();
  for (const [name, impl] of Object.entries(overrides)) fns.set(name, vi.fn(impl));
  return new Proxy({} as MockApi, {
    get(_, prop) {
      if (prop === 'then') return undefined; // not a thenable
      if (!fns.has(prop)) fns.set(prop, vi.fn(() => new Promise(() => {})));
      return fns.get(prop);
    },
  });
}

export interface TestOptions {
  api?: MockApi;
  role?: UserRole;
  providers?: (Provider | EnvironmentProviders)[];
}

/** Configures TestBed with a mocked API, a router and a signed-in user. Returns the API mock. */
export function setupTest(options: TestOptions = {}): MockApi {
  const api = options.api ?? mockApi();
  TestBed.configureTestingModule({
    providers: [{ provide: Api, useValue: api }, provideRouter([]), ...(options.providers ?? [])],
  });
  TestBed.inject(Auth).status.set({ setupRequired: false, authenticated: true, username: 'tester', role: options.role ?? 'Admin' });
  return api;
}

/** Text content of the first element matching `selector`, trimmed and with whitespace collapsed. */
export function text(root: HTMLElement, selector?: string): string {
  const el = selector ? root.querySelector(selector) : root;
  return (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/** The first button whose text or aria-label is `label`, or else the first one containing it. */
export function button(root: HTMLElement, label: string): HTMLButtonElement {
  const buttons = [...root.querySelectorAll('button')];
  const name = (b: HTMLButtonElement) => [b.textContent?.replace(/\s+/g, ' ').trim() ?? '', b.getAttribute('aria-label') ?? ''];
  const found = buttons.find(b => name(b).includes(label)) ?? buttons.find(b => name(b).some(n => n.includes(label)));
  if (!found) throw new Error(`No button "${label}"`);
  return found;
}

/** Lets pending API promises resolve, then waits for change detection. `whenStable` alone doesn't wait for promises started in `ngOnInit`. */
export async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 2; i++) {
    await new Promise(resolve => setTimeout(resolve));
    await fixture.whenStable();
  }
}

/** Types `value` into the input, select or textarea matching `selector`. */
export function type(root: HTMLElement, selector: string, value: string): void {
  const el = root.querySelector<HTMLInputElement>(selector);
  if (!el) throw new Error(`No input "${selector}"`);
  el.value = value;
  el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input'));
}
