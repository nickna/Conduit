import { createTheme, MantineColorsTuple } from '@mantine/core';

const conduitBlue: MantineColorsTuple = [
  '#e3f2fd',
  '#bbdefb',
  '#90caf9',
  '#64b5f6',
  '#42a5f5',
  '#2196f3',
  '#1e88e5',
  '#1976d2',
  '#1565c0',
  '#0d47a1'
];

const conduitGray: MantineColorsTuple = [
  '#fafafa',
  '#f5f5f5',
  '#eeeeee',
  '#e0e0e0',
  '#bdbdbd',
  '#9e9e9e',
  '#757575',
  '#616161',
  '#424242',
  '#212121'
];

export const theme = createTheme({
  primaryColor: 'conduit-blue',
  colors: {
    // Palette names form the CSS custom-property names consumed by chat styles.
    'conduit-blue': conduitBlue,
    'conduit-gray': conduitGray,
  },
  fontFamily: 'Inter, system-ui, -apple-system, sans-serif',
  fontFamilyMonospace: 'JetBrains Mono, Consolas, Monaco, monospace',
  headings: {
    fontFamily: 'Inter, system-ui, -apple-system, sans-serif',
    fontWeight: '600',
  },
  radius: {
    xs: '4px',
    sm: '6px',
    md: '8px',
    lg: '12px',
    xl: '16px',
  },
  spacing: {
    xs: '8px',
    sm: '12px',
    md: '16px',
    lg: '24px',
    xl: '32px',
  },
  shadows: {
    xs: '0 1px 3px rgba(0, 0, 0, 0.05), 0 1px 2px rgba(0, 0, 0, 0.1)',
    sm: '0 1px 3px rgba(0, 0, 0, 0.1), 0 1px 2px rgba(0, 0, 0, 0.06)',
    md: '0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06)',
    lg: '0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -2px rgba(0, 0, 0, 0.05)',
    xl: '0 20px 25px -5px rgba(0, 0, 0, 0.1), 0 10px 10px -5px rgba(0, 0, 0, 0.04)',
  },
  components: {
    Button: {
      defaultProps: {
        size: 'sm',
      },
    },
    TextInput: {
      defaultProps: {
        size: 'sm',
      },
    },
    Select: {
      defaultProps: {
        size: 'sm',
      },
    },
    Card: {
      defaultProps: {
        shadow: 'sm',
        radius: 'md',
        withBorder: true,
      },
    },
    Paper: {
      defaultProps: {
        shadow: 'xs',
        radius: 'md',
      },
    },
  },
});
