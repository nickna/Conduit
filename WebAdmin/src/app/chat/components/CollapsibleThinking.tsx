import { useState } from 'react';
import { Paper, Group, ActionIcon, Text, Collapse } from '@mantine/core';
import { IconChevronDown, IconChevronUp } from '@tabler/icons-react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface CollapsibleThinkingProps {
  content: string;
  icon: string;
  title: string;
}

export function CollapsibleThinking({ content, icon, title }: CollapsibleThinkingProps) {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <Paper
      p="sm"
      radius="md"
      withBorder
      style={{
        backgroundColor: 'var(--mantine-color-gray-light)',
        cursor: 'pointer',
        transition: 'all 0.2s ease',
        userSelect: 'none'
      }}
      className="thinking-block"
      onClick={() => setIsOpen(!isOpen)}
    >
      <Group gap="xs" wrap="nowrap">
        <ActionIcon
          variant="subtle"
          size="sm"
          style={{ pointerEvents: 'none' }}
        >
          {isOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
        </ActionIcon>
        <Text size="sm" fw={500} style={{ flex: 1 }}>
          {icon} {title}
        </Text>
        <Text size="xs" c="dimmed">
          {isOpen ? 'Click to collapse' : 'Click to expand'}
        </Text>
      </Group>
      <Collapse expanded={isOpen}>
        <div style={{ marginTop: '0.5rem', paddingLeft: '1.5rem' }}>
          <ReactMarkdown remarkPlugins={[remarkGfm]}>{content}</ReactMarkdown>
        </div>
      </Collapse>
    </Paper>
  );
}
