/**
 * Correctness tests for MediaGallery component
 * Tests rendering behavior with various item counts and virtualization settings
 */
import React from 'react';
import { render, screen } from '@/app/test-utils';
import '@testing-library/jest-dom';
import { MediaGallery } from '../MediaGallery';

// Mock @tanstack/react-virtual
jest.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: jest.fn((options: { count: number; enabled: boolean }) => {
    const { count, enabled } = options;
    // If virtualization is not enabled, return empty mock
    if (!enabled) {
      return {
        getTotalSize: () => 0,
        getVirtualItems: () => [],
        scrollToIndex: jest.fn()
      };
    }

    // Simulate rendering first 10 virtual items (as if they're visible)
    const virtualItems = [...Array(Math.min(10, count)).keys()].map((i) => ({
      key: i,
      index: i,
      start: i * 350,
      size: 350,
      end: (i + 1) * 350
    }));

    return {
      getTotalSize: () => count * 350,
      getVirtualItems: () => virtualItems,
      scrollToIndex: jest.fn()
    };
  })
}));

interface TestItem {
  id: string;
  name: string;
}

describe('MediaGallery Rendering Tests', () => {
  const createTestItems = (count: number): TestItem[] => {
    return Array.from({ length: count }, (item, i) => ({
      id: `item-${i}`,
      name: `Test Item ${i}`
    }));
  };

  const renderTestCard = (item: TestItem) => (
    <div key={item.id} data-testid={`card-${item.id}`}>
      {item.name}
    </div>
  );

  describe('Rendering behavior', () => {
    it('should render small galleries (10 items) without virtualization', () => {
      const items = createTestItems(10);

      render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
        />
      );

      expect(screen.getByTestId('card-item-0')).toBeInTheDocument();
      expect(screen.getByTestId('card-item-9')).toBeInTheDocument();
    });

    it('should render medium galleries (50 items) efficiently', () => {
      const items = createTestItems(50);

      render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
        />
      );

      // All items should be rendered without virtualization at threshold
      expect(screen.getByTestId('card-item-0')).toBeInTheDocument();
      expect(screen.getByTestId('card-item-49')).toBeInTheDocument();
    });

    it('should enable virtualization for large galleries (100+ items)', () => {
      const items = createTestItems(100);

      const { container } = render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
        />
      );

      // With virtualization, should render a scrollable container
      const scrollContainer = container.querySelector('[style*="overflow"]');
      expect(scrollContainer).toBeInTheDocument();

      // Should render only visible items (mocked to return first 10)
      expect(screen.getByTestId('card-item-0')).toBeInTheDocument();
      expect(screen.getByTestId('card-item-9')).toBeInTheDocument();

      console.warn('Virtualization enabled for 100+ items');
    });

    it('should respect custom virtualization threshold', () => {
      const items = createTestItems(30);

      const { container } = render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
          virtualizationThreshold={25}
        />
      );

      // Should enable virtualization since 30 > 25
      const scrollContainer = container.querySelector('[style*="overflow"]');
      expect(scrollContainer).toBeInTheDocument();

      // Should render only visible items (mocked to return first 10)
      expect(screen.getByTestId('card-item-0')).toBeInTheDocument();
      expect(screen.getByTestId('card-item-9')).toBeInTheDocument();

      console.warn('Custom virtualization threshold respected');
    });

    it('should allow disabling virtualization explicitly', () => {
      const items = createTestItems(100);

      render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
          enableVirtualization={false}
        />
      );

      // All items should be rendered
      expect(screen.getByTestId('card-item-0')).toBeInTheDocument();
      expect(screen.getByTestId('card-item-99')).toBeInTheDocument();

      console.warn('Virtualization disabled, rendered all 100 items');
    });
  });

  describe('Memory Efficiency', () => {
    it('should not create excessive DOM nodes with virtualization', () => {
      const items = createTestItems(100);

      const { container } = render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
        />
      );

      // With virtualization, should only render visible items
      // The mock returns first 10 items
      const cards = container.querySelectorAll('[data-testid^="card-"]');

      // Should render only ~10 items despite having 100 total
      expect(cards.length).toBe(10);
      expect(cards.length).toBeLessThan(100);

      console.warn(`Virtualized gallery rendered ${cards.length} DOM nodes for 100 items`);
    });
  });

  describe('Re-render Performance', () => {
    it('should handle item updates efficiently', () => {
      const items = createTestItems(20);

      const { rerender } = render(
        <MediaGallery
          items={items}
          renderCard={renderTestCard}
        />
      );

      expect(screen.getByTestId('card-item-19')).toBeInTheDocument();
      const updatedItems = createTestItems(5);
      rerender(
        <MediaGallery
          items={updatedItems}
          renderCard={renderTestCard}
        />
      );
      expect(screen.getByTestId('card-item-4')).toBeInTheDocument();
      expect(screen.queryByTestId('card-item-19')).not.toBeInTheDocument();
    });
  });

  describe('Empty state', () => {
    it('should render the configured empty state', () => {

      render(
        <MediaGallery
          items={[]}
          renderCard={renderTestCard}
          emptyTitle="No items"
          emptyMessage="Add some items to get started"
        />
      );

      expect(screen.getByText('No items')).toBeInTheDocument();
    });
  });
});
