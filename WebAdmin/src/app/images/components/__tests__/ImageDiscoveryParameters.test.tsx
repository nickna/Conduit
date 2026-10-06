import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import { DynamicParameters } from '@/components/parameters/DynamicParameters';
import { useMediaInterface } from '@/app/hooks/useMediaInterface';
import { getBrowserGatewayClient } from '@/lib/client/browserGatewayClient';
import { ModelCapability } from '@/lib/gateway-api';

jest.mock('@/lib/client/browserGatewayClient', () => ({
  getBrowserGatewayClient: jest.fn(),
}));

const mockedGetBrowserGatewayClient = jest.mocked(getBrowserGatewayClient);

function DiscoveryParameterHarness() {
  const [model, setModel] = useState('server-image-model');
  const media = useMediaInterface({
    capability: ModelCapability.ImageGeneration,
    currentModel: model,
    onModelChange: setModel,
    onError: jest.fn(),
    parameterPersistPrefix: 'image',
  });

  if (!media.selectedDiscoveryModel?.parameters) return null;

  return (
    <DynamicParameters
      parameters={media.selectedDiscoveryModel.parameters}
      values={media.parameterState.values}
      onChange={media.parameterState.updateValues}
      context="image"
      collapsible={false}
    />
  );
}

describe('image discovery parameter data flow', () => {
  beforeEach(() => {
    localStorage.clear();
    mockedGetBrowserGatewayClient.mockResolvedValue({
      discovery: {
        getModelsByCapability: jest.fn().mockResolvedValue({
          data: [{
            id: 'server-image-model',
            parameters: JSON.stringify({
              quality: {
                type: 'select',
                label: 'Quality from discovery',
                default: 'standard',
                options: [
                  { value: 'standard', label: 'Standard' },
                  { value: 'hd', label: 'HD' },
                ],
              },
              style: {
                type: 'select',
                label: 'Style from discovery',
                default: 'natural',
                options: [
                  { value: 'natural', label: 'Natural' },
                  { value: 'vivid', label: 'Vivid' },
                ],
              },
            }),
          }],
          count: 1,
        }),
      },
    } as never);
  });

  it('renders controls supplied by the real discovery query path', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <MantineProvider>
          <DiscoveryParameterHarness />
        </MantineProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Quality from discovery')).toBeVisible();
    expect(screen.getByText('Style from discovery')).toBeVisible();
    expect(mockedGetBrowserGatewayClient).toHaveBeenCalledTimes(1);
  });
});
