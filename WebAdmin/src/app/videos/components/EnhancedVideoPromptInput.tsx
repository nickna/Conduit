'use client';

import { useState, useCallback } from 'react';
import { Button, Group, Text, Stack } from '@mantine/core';
import { IconVideo } from '@tabler/icons-react';
import { useVideoStore } from '../hooks/useVideoStore';
import { useEnhancedVideoGeneration } from '../hooks/useEnhancedVideoGeneration';
import { MediaPromptInput } from '@/app/components/media';
import { MediaGenerationStatus } from '@/app/types/media';
interface VideoPromptInputProps {
  dynamicParameters?: Record<string, unknown>;
}

export default function EnhancedVideoPromptInput({ dynamicParameters }: VideoPromptInputProps) {
  const [prompt, setPrompt] = useState('');
  const { settings, currentTask, setError } = useVideoStore();
  
  const { generateVideo, isGenerating } = useEnhancedVideoGeneration();

  const handleSubmit = useCallback(async () => {
    if (!prompt.trim()) {
      setError('Please enter a prompt');
      return;
    }

    if (!settings.model) {
      setError('Please select a model');
      return;
    }

    setError(null);
    
    try {
      await generateVideo({
        prompt: prompt.trim(),
        settings,
        dynamicParameters,
      });
      // Clear prompt after successful submission
      setPrompt('');
    } catch (error) {
      // Error is handled in the hook
      console.error('Error in VideoPromptInput:', error);
    }
  }, [prompt, settings, generateVideo, setError, dynamicParameters]);

  const isDisabled = isGenerating || !!(currentTask && (currentTask.status === MediaGenerationStatus.Pending || currentTask.status === MediaGenerationStatus.Generating));

  return (
    <form onSubmit={(e) => { e.preventDefault(); void handleSubmit(); }}>
      <Stack gap="md">
        <MediaPromptInput
          value={prompt}
          onChange={setPrompt}
          onSubmit={() => void handleSubmit()}
          label="Video Prompt"
          placeholder="Describe the video you want to generate..."
          disabled={isDisabled}
          isLoading={isGenerating}
          submitShortcut="enter"
          showCharCount={true}
          additionalInfo={
            currentTask && (
              <Text size="sm" c="blue" fw={500}>
                Video generation in progress...
              </Text>
            )
          }
        />
        
        <Group justify="flex-end">
          <Button
            type="submit"
            disabled={isDisabled || !prompt.trim()}
            leftSection={<IconVideo size={16} />}
            loading={isGenerating}
          >
            {isGenerating ? 'Generating...' : 'Generate Video'}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
