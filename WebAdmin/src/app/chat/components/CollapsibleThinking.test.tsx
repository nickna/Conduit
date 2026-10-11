import { useEffect } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/styles/theme';
import { CollapsibleThinking } from './CollapsibleThinking';

const mockMount = jest.fn();
const mockCleanup = jest.fn();

jest.mock('react-markdown', () => function MockMarkdown({ children }: { children: string }) {
  useEffect(() => {
    mockMount();
    return () => { mockCleanup(); };
  }, []);
  return <p>{children}</p>;
});
jest.mock('remark-gfm', () => jest.fn());

it('toggles reasoning visibility while keeping the hidden content mounted', async () => {
  render(
    <MantineProvider env="test" theme={theme}>
      <CollapsibleThinking content="Model reasoning" icon="💭" title="Thinking" />
    </MantineProvider>
  );

  expect(screen.getByText('Model reasoning')).not.toBeVisible();
  expect(mockMount).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByText('Click to expand'));
  await waitFor(() => expect(screen.getByText('Model reasoning')).toBeVisible());
  fireEvent.click(screen.getByText('Click to collapse'));
  await waitFor(() => expect(screen.getByText('Model reasoning')).not.toBeVisible());
  expect(mockMount).toHaveBeenCalledTimes(1);
  expect(mockCleanup).not.toHaveBeenCalled();
});
