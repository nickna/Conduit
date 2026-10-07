import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MantineProvider } from '@mantine/core';
import { theme } from './theme';

it('defines the color variables used by global and chat styles in the real Mantine theme', () => {
  const styles = ['src/app/globals.css', 'src/app/chat/styles.css']
    .map(file => readFileSync(resolve(file), 'utf8'));
  const renderedTheme = renderToStaticMarkup(
    createElement(MantineProvider, { theme, deduplicateCssVariables: false }),
  );
  const definitions = new Set(
    [...[...styles, renderedTheme].join('\n').matchAll(/(--[\w-]+)\s*:/g)]
      .map(match => match[1]),
  );
  const references = new Set(
    [...styles.join('\n').matchAll(/var\((--[\w-]+)/g)].map(match => match[1]),
  );

  expect([...references].filter(reference => !definitions.has(reference))).toEqual([]);
  expect(theme.colors?.[theme.primaryColor!]).toBeDefined();
});
