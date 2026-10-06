import { useEffect, useState, useMemo } from 'react';

interface UsePaginatedDataOptions {
  defaultPageSize?: number;
}

export function usePaginatedData<T>(
  data: T[] | undefined,
  options: UsePaginatedDataOptions = {}
) {
  const { defaultPageSize = 20 } = options;
  
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(defaultPageSize);

  const paginatedData = useMemo(() => {
    if (!data || !Array.isArray(data)) return [];
    
    const start = (page - 1) * pageSize;
    const end = start + pageSize;
    
    return data.slice(start, end);
  }, [data, page, pageSize]);

  const totalItems = Array.isArray(data) ? data.length : 0;
  const totalPages = Math.ceil(totalItems / pageSize);

  useEffect(() => {
    if (page > totalPages && totalPages > 0) setPage(1);
  }, [page, totalPages]);

  const handlePageChange = (newPage: number) => {
    setPage(newPage);
  };

  const handlePageSizeChange = (newPageSize: number) => {
    setPageSize(newPageSize);
    setPage(1); // Reset to first page when changing page size
  };

  return {
    paginatedData,
    page,
    pageSize,
    totalItems,
    totalPages,
    handlePageChange,
    handlePageSizeChange,
  };
}
