import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import UsageAnalyticsPage from './page';

jest.mock('@/contexts/ThemeContext', () => ({
  useTheme: () => ({ colorScheme: 'dark' }),
}));

describe('UsageAnalyticsPage', () => {
  it('embeds the overview dashboard without exposing credentials', () => {
    render(<MantineProvider><UsageAnalyticsPage /></MantineProvider>);

    const frame = screen.getByTitle('Grafana usage analytics dashboard');
    expect(frame).toHaveAttribute(
      'src',
      '/grafana/d/conduit-overview/conduit-api-overview?kiosk&refresh=30s&theme=dark'
    );
    expect(screen.queryByText(/conduitadmin/i)).not.toBeInTheDocument();
  });

  it('switches the embedded and full Grafana links together', () => {
    render(<MantineProvider><UsageAnalyticsPage /></MantineProvider>);

    fireEvent.click(screen.getByRole('combobox', { name: 'Dashboard' }));
    fireEvent.click(screen.getByRole('option', { name: 'Infrastructure' }));

    const expected = '/grafana/d/conduit-infrastructure/infrastructure?kiosk&refresh=30s&theme=dark';
    expect(screen.getByTitle('Grafana usage analytics dashboard')).toHaveAttribute('src', expected);
    expect(screen.getByRole('link', { name: /open in grafana/i })).toHaveAttribute('href', expected);
  });
});
