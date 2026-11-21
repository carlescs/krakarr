import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Progress } from './progress';

describe('Progress', () => {
  it.each([[42, '42%'], [150, '100%'], [-5, '0%']])('clamps %s to %s', async (value, width) => {
    const fixture = TestBed.createComponent(Progress);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('color', 'bg-emerald-500');
    await fixture.whenStable();
    const bar = fixture.nativeElement.querySelector('.bg-emerald-500') as HTMLElement;
    expect(bar.style.width).toBe(width);
  });
});
