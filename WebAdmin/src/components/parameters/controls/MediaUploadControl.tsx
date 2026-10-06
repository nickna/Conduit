'use client';

import { useRef, useState, useCallback } from 'react';
import { Button, Text, Group, Stack, Progress, Badge, Tooltip, ActionIcon } from '@mantine/core';
import { IconUpload, IconPhoto, IconVideo, IconMusic, IconX } from '@tabler/icons-react';
import { notify } from '@/lib/notifications';
import { getBrowserGatewayClient } from '@/lib/client/browserGatewayClient';
import type { DynamicParameter } from '../types/parameters';

interface MediaUploadControlProps {
  parameter: DynamicParameter;
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
}

export function MediaUploadControl({
  parameter,
  value,
  onChange,
  disabled = false,
}: MediaUploadControlProps) {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState(0);
  const [uploadStatus, setUploadStatus] = useState<'idle' | 'uploading' | 'success' | 'error'>('idle');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);

  // Determine media type from parameter metadata or default to all
  const getAcceptedTypes = useCallback(() => {
    const metadata = parameter.metadata;
    const mediaType = metadata?.mediaType as string | undefined ?? metadata?.type as string | undefined;
    
    switch (mediaType) {
      case 'image':
      case 'Image':
        return 'image/*';
      case 'video':
      case 'Video':
        return 'video/*';
      case 'audio':
      case 'Audio':
        return 'audio/*';
      default:
        return 'image/*,video/*,audio/*';
    }
  }, [parameter.metadata]);

  // Get icon based on media type
  const getIcon = useCallback(() => {
    const metadata = parameter.metadata;
    const mediaType = metadata?.mediaType as string | undefined ?? metadata?.type as string | undefined;
    
    switch (mediaType) {
      case 'image':
      case 'Image':
        return <IconPhoto size={16} />;
      case 'video':
      case 'Video':
        return <IconVideo size={16} />;
      case 'audio':
      case 'Audio':
        return <IconMusic size={16} />;
      default:
        return <IconUpload size={16} />;
    }
  }, [parameter.metadata]);

  const handleFileSelect = useCallback(async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;

    // Reset state
    setErrorMessage(null);
    setUploadStatus('uploading');
    setUploading(true);
    setUploadProgress(0);
    setFileName(file.name);

    try {
      // Get Core client with ephemeral key
      const coreClient = await getBrowserGatewayClient();

      // Determine media type from file MIME type
      let mediaType: 'Image' | 'Video' | 'Audio' | undefined;
      if (file.type.startsWith('image/')) {
        mediaType = 'Image';
      } else if (file.type.startsWith('video/')) {
        mediaType = 'Video';
      } else if (file.type.startsWith('audio/')) {
        mediaType = 'Audio';
      }

      // Validate file size using Core SDK media service
      if ('media' in coreClient && coreClient.media) {
        const mediaService = coreClient.media;
        const validation = mediaService.validateFileSize(file, mediaType);
        if (!validation.valid) {
          throw new Error(validation.message ?? 'File validation failed');
        }

        // Upload the file
        const result = await mediaService.upload(file, {
          mediaType,
          onProgress: (loaded: number, total: number) => {
            const percent = Math.round((loaded / total) * 100);
            setUploadProgress(percent);
          },
        }) as { success: boolean; url: string };

        // Success!
        setUploadStatus('success');
        setUploading(false);
        
        // Update the parameter value with the URL
        onChange(result.url);
      } else {
        throw new Error('Media service not available');
      }


      // Show success notification
      notify.success(`${file.name} has been uploaded successfully`, 'Upload successful');

      // Reset status after a delay
      setTimeout(() => {
        setUploadStatus('idle');
        setUploadProgress(0);
      }, 3000);

    } catch (error) {
      console.error('Upload error:', error);
      setUploadStatus('error');
      setUploading(false);
      
      const message = error instanceof Error ? error.message : 'Failed to upload file';
      setErrorMessage(message);

      // Show error notification
      notify.error(message);
    }

    // Clear the input so the same file can be selected again
    if (fileInputRef.current) {
      fileInputRef.current.value = '';
    }
  }, [onChange]);

  const handleClear = useCallback(() => {
    onChange('');
    setFileName(null);
    setUploadStatus('idle');
    setUploadProgress(0);
    setErrorMessage(null);
  }, [onChange]);

  const handleClick = useCallback(() => {
    if (!disabled && !uploading) {
      fileInputRef.current?.click();
    }
  }, [disabled, uploading]);

  // Extract filename from URL if value is set
  const displayValue = value ? (value.split('/').pop() ?? value) : null;

  return (
    <Stack gap="xs">
      <input
        ref={fileInputRef}
        type="file"
        accept={getAcceptedTypes()}
        onChange={(event) => {
          void handleFileSelect(event);
        }}
        style={{ display: 'none' }}
        disabled={disabled || uploading}
      />

      <Group gap="xs">
        <Button
          onClick={(e) => {
            e.preventDefault();
            void handleClick();
          }}
          disabled={disabled || uploading}
          loading={uploading}
          leftSection={getIcon()}
          variant={value ? 'light' : 'filled'}
          size="sm"
        >
          {(() => {
            if (uploading) return 'Uploading...';
            if (value) return 'Replace';
            return 'Upload';
          })()}
        </Button>

        {value && (
          <Tooltip label="Clear">
            <ActionIcon
              onClick={handleClear}
              disabled={disabled || uploading}
              variant="subtle"
              color="gray"
              size="sm"
            >
              <IconX size={16} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      {/* Upload progress */}
      {uploading && (
        <Progress
          value={uploadProgress}
          size="sm"
          color="blue"
          striped
          animated
        />
      )}

      {/* Current value or file name */}
      {(displayValue ?? fileName) && (
        <Group gap="xs">
          <Text size="xs" c="dimmed">
            {uploadStatus === 'uploading' ? 'Uploading:' : 'Current:'}
          </Text>
          <Badge
            size="sm"
            variant="light"
            color={
              (() => {
                if (uploadStatus === 'success') return 'green';
                if (uploadStatus === 'error') return 'red';
                if (uploadStatus === 'uploading') return 'blue';
                return 'gray';
              })()
            }
          >
            {fileName ?? displayValue ?? ''}
          </Badge>
        </Group>
      )}

      {/* Error message */}
      {errorMessage && (
        <Text size="xs" c="red">
          {errorMessage}
        </Text>
      )}

      {/* Success message */}
      {uploadStatus === 'success' && !errorMessage && (
        <Text size="xs" c="green">
          Upload successful!
        </Text>
      )}

      {/* Help text */}
      {parameter.description && !uploading && uploadStatus === 'idle' && (
        <Text size="xs" c="dimmed">
          {parameter.description}
        </Text>
      )}
    </Stack>
  );
}