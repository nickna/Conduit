'use client';

import { MantineProvider as BaseMantineProvider, v8CssVariablesResolver } from '@mantine/core';
import { ModalsProvider } from '@mantine/modals';
import { Notifications } from '@mantine/notifications';
import { theme } from '@/styles/theme';
import { useTheme } from '@/contexts/ThemeContext';

import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dates/styles.css';
import '@mantine/charts/styles.css';
import '@mantine/code-highlight/styles.css';

interface MantineProviderProps {
  children: React.ReactNode;
}

export function MantineProvider({ children }: MantineProviderProps) {
  const { colorScheme } = useTheme();

  return (
    <BaseMantineProvider theme={theme} forceColorScheme={colorScheme} cssVariablesResolver={v8CssVariablesResolver}>
      <ModalsProvider>
        <Notifications position="top-right" zIndex={9999} pauseResetOnHover="notification" />
        {children}
      </ModalsProvider>
    </BaseMantineProvider>
  );
}