import React, { useEffect } from 'react';
import Button from 'Components/Link/Button';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Column from 'Components/Table/Column';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import Popover from 'Components/Tooltip/Popover';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { sizes, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './InteractiveSearchStatus.css';

type IndexerSearchStatusType =
  | 'searched'
  | 'cached'
  | 'skipped'
  | 'notWaitedFor'
  | 'failed'
  | 'timedOut';

interface IndexerSearchStatus {
  indexerId: number;
  name: string;
  priority: number;
  status: IndexerSearchStatusType;
  releaseCount: number;
  message?: string;
}

interface IndexerOptions {
  earlySearchReturn: boolean;
  searchIndexersInPriorityOrder: boolean;
  searchResultCacheLifetime: number;
}

interface ReleaseSearchStatus {
  cachedAt?: string;
  indexers: IndexerSearchStatus[];
}

const statusLabelKeys: Record<IndexerSearchStatusType, string> = {
  searched: 'IndexerSearchStatusSearched',
  cached: 'IndexerSearchStatusCached',
  skipped: 'IndexerSearchStatusSkipped',
  notWaitedFor: 'IndexerSearchStatusNotWaitedFor',
  failed: 'IndexerSearchStatusFailed',
  timedOut: 'IndexerSearchStatusTimedOut',
};

const remainingStatuses: IndexerSearchStatusType[] = [
  'skipped',
  'notWaitedFor',
  'failed',
  'timedOut',
];

const columns: Column[] = [
  {
    name: 'name',
    label: () => translate('Indexer'),
    isVisible: true,
  },
  {
    name: 'priority',
    label: () => translate('Priority'),
    isVisible: true,
  },
  {
    name: 'status',
    label: () => translate('Status'),
    isVisible: true,
  },
  {
    name: 'releaseCount',
    label: () => translate('Results'),
    isVisible: true,
  },
];

interface InteractiveSearchStatusProps {
  searchPayload: Record<string, unknown>;
  isFetching: boolean;
  onSearchRemainingPress: () => void;
  onSearchAgainPress: () => void;
}

function InteractiveSearchStatus({
  searchPayload,
  isFetching,
  onSearchRemainingPress,
  onSearchAgainPress,
}: InteractiveSearchStatusProps) {
  const query = new URLSearchParams(
    Object.entries(searchPayload).map(([key, value]) => [key, String(value)])
  ).toString();

  const { data, refetch } = useApiQuery<ReleaseSearchStatus>({
    url: `/release/searchstatus?${query}`,
  });

  const { data: options } = useApiQuery<IndexerOptions>({
    url: '/config/indexer',
  });

  // The status belongs to the last finished search, so it is read again once a search finishes
  useEffect(() => {
    if (!isFetching) {
      refetch();
    }
  }, [isFetching, refetch]);

  const indexers = isFetching ? [] : data?.indexers ?? [];
  const delivered = indexers.filter(
    (i) => i.status === 'searched' || i.status === 'cached'
  );
  const cachedCount = indexers.filter((i) => i.status === 'cached').length;
  const hasRemaining = indexers.some((i) =>
    remainingStatuses.includes(i.status)
  );

  let summary = translate('InteractiveSearchStatusSummary', {
    searched: delivered.length,
    total: indexers.length,
  });

  if (cachedCount && data?.cachedAt) {
    const minutes = Math.round(
      (Date.now() - new Date(data.cachedAt).getTime()) / 60000
    );

    const cacheText = translate(
      cachedCount === delivered.length
        ? 'InteractiveSearchStatusFromCache'
        : 'InteractiveSearchStatusPartlyFromCache',
      { minutes }
    );

    summary = `${summary}, ${cacheText}`;
  }

  return (
    <>
      {indexers.length ? (
        <Popover
          anchor={<span className={styles.summary}>{summary}</span>}
          title={translate('InteractiveSearchStatus')}
          position={tooltipPositions.BOTTOM}
          body={
            <div className={styles.body}>
              <Table columns={columns}>
                <TableBody>
                  {indexers.map((indexer) => (
                    <TableRow key={indexer.indexerId}>
                      <TableRowCell>{indexer.name}</TableRowCell>
                      <TableRowCell>{indexer.priority}</TableRowCell>
                      <TableRowCell>
                        {translate(statusLabelKeys[indexer.status])}
                        {indexer.message ? (
                          <div className={styles.message}>
                            {indexer.message}
                          </div>
                        ) : null}
                      </TableRowCell>
                      <TableRowCell>{indexer.releaseCount}</TableRowCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>

              {options?.earlySearchReturn &&
              options.searchIndexersInPriorityOrder ? (
                <p>{translate('InteractiveSearchStatusPriorityOrderNote')}</p>
              ) : null}

              {options?.earlySearchReturn ? (
                <p>
                  {translate('InteractiveSearchStatusEarlySearchReturnNote')}
                </p>
              ) : null}

              {options?.searchResultCacheLifetime ? (
                <p>
                  {translate('InteractiveSearchStatusCacheNote', {
                    minutes: options.searchResultCacheLifetime,
                  })}
                </p>
              ) : null}
            </div>
          }
        />
      ) : null}

      {hasRemaining ? (
        <Button
          size={sizes.SMALL}
          isDisabled={isFetching}
          onPress={onSearchRemainingPress}
        >
          {translate('SearchRemainingIndexers')}
        </Button>
      ) : null}

      <Button
        size={sizes.SMALL}
        isDisabled={isFetching}
        onPress={onSearchAgainPress}
      >
        {translate('SearchAgain')}
      </Button>
    </>
  );
}

export default InteractiveSearchStatus;
