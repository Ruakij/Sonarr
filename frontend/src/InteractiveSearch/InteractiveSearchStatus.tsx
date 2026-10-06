import classNames from 'classnames';
import React, { ReactNode, useEffect } from 'react';
import Icon from 'Components/Icon';
import Label, { LabelProps } from 'Components/Label';
import Button from 'Components/Link/Button';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Column from 'Components/Table/Column';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import Popover from 'Components/Tooltip/Popover';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { icons, kinds, sizes, tooltipPositions } from 'Helpers/Props';
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
  cachedAt?: string;
  queryCount?: number;
  medianResponseMs?: number;
  historyCount?: number;
  historyMedianMs?: number;
  historyLowMs?: number;
  historyHighMs?: number;
}

interface IndexerOptions {
  earlySearchReturn: boolean;
  earlySearchReturnRequiredPriority: number;
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

const statusKinds: Record<IndexerSearchStatusType, LabelProps['kind']> = {
  searched: kinds.SUCCESS,
  cached: kinds.INFO,
  skipped: kinds.DEFAULT,
  notWaitedFor: kinds.WARNING,
  failed: kinds.DANGER,
  timedOut: kinds.DANGER,
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
    className: styles.numericHeader,
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
    className: styles.numericHeader,
    isVisible: true,
  },
  {
    name: 'time',
    label: () => translate('InteractiveSearchStatusTime'),
    className: styles.numericHeader,
    isVisible: true,
  },
];

function formatSeconds(ms: number) {
  return `${(ms / 1000).toFixed(1)} s`;
}

function getAgeMinutes(cachedAt: string) {
  return Math.round((Date.now() - new Date(cachedAt).getTime()) / 60000);
}

// Translations mark setting names with **
function renderBold(text: string): ReactNode[] {
  return text
    .split('**')
    .map((part, i) => (i % 2 ? <strong key={i}>{part}</strong> : part));
}

function getTime(indexer: IndexerSearchStatus) {
  if (indexer.medianResponseMs == null) {
    return null;
  }

  const time = formatSeconds(indexer.medianResponseMs);

  return indexer.queryCount && indexer.queryCount > 1
    ? `${time} (${indexer.queryCount})`
    : time;
}

function getTimeTooltip(indexer: IndexerSearchStatus) {
  if (indexer.medianResponseMs == null) {
    return undefined;
  }

  const median = formatSeconds(indexer.medianResponseMs);
  const queryCount = indexer.queryCount ?? 1;

  const tooltip =
    queryCount === 1
      ? translate('InteractiveSearchStatusTimeTooltipSingle', { median })
      : translate('InteractiveSearchStatusTimeTooltipMultiple', {
          queryCount,
          median,
        });

  if (
    !indexer.historyCount ||
    indexer.historyMedianMs == null ||
    indexer.historyLowMs == null ||
    indexer.historyHighMs == null
  ) {
    return tooltip;
  }

  const history = translate('InteractiveSearchStatusTimeTooltipHistory', {
    historyCount: indexer.historyCount,
    historyMedian: formatSeconds(indexer.historyMedianMs),
    historyLow: formatSeconds(indexer.historyLowMs),
    historyHigh: formatSeconds(indexer.historyHighMs),
  });

  return `${tooltip}. ${history}`;
}

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
  const cachedAges = indexers
    .filter((i) => i.status === 'cached' && i.cachedAt)
    .map((i) => getAgeMinutes(i.cachedAt as string));
  const hasRemaining = indexers.some((i) =>
    remainingStatuses.includes(i.status)
  );
  const hasFailed = indexers.some(
    (i) => i.status === 'failed' || i.status === 'timedOut'
  );

  // Indexers up to Required Priority are searched together as the first group
  const requiredPriority =
    options?.earlySearchReturn && options.earlySearchReturnRequiredPriority > 0
      ? options.earlySearchReturnRequiredPriority
      : 0;

  // Priority groups only exist when indexers are searched in priority order
  const hasGroups = Boolean(
    options?.earlySearchReturn && options.searchIndexersInPriorityOrder
  );
  const getGroup = (indexer: IndexerSearchStatus) =>
    Math.max(indexer.priority, requiredPriority);
  const sortedIndexers = [...indexers].sort(
    (a, b) => a.priority - b.priority || a.name.localeCompare(b.name)
  );

  let summary = translate('InteractiveSearchStatusSummary', {
    searched: delivered.length,
    total: indexers.length,
  });

  if (cachedAges.length) {
    const minAge = Math.min(...cachedAges);
    const maxAge = Math.max(...cachedAges);

    const cacheText = translate(
      cachedAges.length === delivered.length
        ? 'InteractiveSearchStatusFromCache'
        : 'InteractiveSearchStatusPartlyFromCache',
      { minutes: minAge === maxAge ? minAge : `${minAge}-${maxAge}` }
    );

    summary = `${summary}, ${cacheText}`;
  }

  let statusIcon = null;

  if (hasFailed) {
    statusIcon = (
      <Icon
        className={styles.statusIcon}
        name={icons.WARNING}
        kind={kinds.DANGER}
      />
    );
  } else if (cachedAges.length) {
    statusIcon = <Icon className={styles.statusIcon} name={icons.HISTORY} />;
  }

  return (
    <>
      {indexers.length ? (
        <Popover
          anchor={
            <span>
              {statusIcon}
              <span className={styles.summary}>{summary}</span>
            </span>
          }
          title={translate('InteractiveSearchStatus')}
          position={tooltipPositions.BOTTOM}
          body={
            <div className={styles.body}>
              <Table columns={columns}>
                <TableBody>
                  {sortedIndexers.map((indexer, index) => (
                    <TableRow
                      key={indexer.indexerId}
                      className={classNames(
                        styles.row,
                        hasGroups &&
                          index > 0 &&
                          getGroup(indexer) !==
                            getGroup(sortedIndexers[index - 1]) &&
                          styles.groupStart
                      )}
                    >
                      <TableRowCell>{indexer.name}</TableRowCell>
                      <TableRowCell className={styles.numericCell}>
                        {requiredPriority > 0 &&
                        indexer.priority <= requiredPriority ? (
                          <Label className={styles.requiredLabel}>
                            {translate('Required')}
                          </Label>
                        ) : null}
                        {indexer.priority}
                      </TableRowCell>
                      <TableRowCell>
                        <Label kind={statusKinds[indexer.status]}>
                          {translate(statusLabelKeys[indexer.status])}
                        </Label>
                        {indexer.status === 'cached' && indexer.cachedAt ? (
                          <span
                            className={styles.age}
                            title={new Date(indexer.cachedAt).toLocaleString()}
                          >
                            {translate('InteractiveSearchStatusCacheAge', {
                              minutes: getAgeMinutes(indexer.cachedAt),
                            })}
                          </span>
                        ) : null}
                        {indexer.message ? (
                          <div className={styles.message}>
                            {indexer.message}
                          </div>
                        ) : null}
                      </TableRowCell>
                      <TableRowCell className={styles.numericCell}>
                        {indexer.releaseCount}
                      </TableRowCell>
                      <TableRowCell
                        className={styles.numericCell}
                        title={getTimeTooltip(indexer)}
                      >
                        {getTime(indexer)}
                      </TableRowCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>

              {options?.earlySearchReturn &&
              options.searchIndexersInPriorityOrder ? (
                <p>
                  {renderBold(
                    translate('InteractiveSearchStatusPriorityOrderNote')
                  )}
                </p>
              ) : null}

              {options?.earlySearchReturn ? (
                <p>
                  {renderBold(
                    translate('InteractiveSearchStatusEarlySearchReturnNote')
                  )}
                </p>
              ) : null}

              {options?.searchResultCacheLifetime ? (
                <p>
                  {renderBold(
                    translate('InteractiveSearchStatusCacheNote', {
                      minutes: options.searchResultCacheLifetime,
                    })
                  )}
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
          title={translate('InteractiveSearchSearchRemainingTooltip')}
          onPress={onSearchRemainingPress}
        >
          {translate('SearchRemainingIndexers')}
        </Button>
      ) : null}

      <Button
        size={sizes.SMALL}
        isDisabled={isFetching}
        title={translate('InteractiveSearchSearchAgainTooltip')}
        onPress={onSearchAgainPress}
      >
        {translate('SearchAgain')}
      </Button>
    </>
  );
}

export default InteractiveSearchStatus;
