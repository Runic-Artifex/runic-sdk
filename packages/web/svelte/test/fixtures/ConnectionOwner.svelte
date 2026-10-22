<script lang="ts">
  import { useBridgeConnection } from "../../src/views/index.js";
  const connection = useBridgeConnection();
</script>

<output>{connection.state}</output>
