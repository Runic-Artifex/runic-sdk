let current: number | undefined;
const events = new EventSource("/__runic_dev/events");
events.onmessage = ({ data }) => {
  const generation = Number(data);
  if (!Number.isSafeInteger(generation)) return;
  if (current !== undefined && generation !== current) location.reload();
  current = generation;
};
