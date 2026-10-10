import { test } from 'node:test';
import assert from 'node:assert/strict';
import { enforceCss } from './css-warning-policy.mjs';
test('new CSS debt fails per file/rule while reductions remain allowed', () => {
  enforceCss({ 'globals.css:rule': 2 }, { 'globals.css:rule': 3 });
  assert.throws(() => enforceCss({ 'globals.css:rule': 4 }, { 'globals.css:rule': 3 }));
  assert.throws(() => enforceCss({ 'new.css:rule': 1 }, { 'globals.css:rule': 3 }));
  assert.throws(() => enforceCss({ 'globals.css:new-rule': 1 }, { 'globals.css:rule': 3 }));
});
