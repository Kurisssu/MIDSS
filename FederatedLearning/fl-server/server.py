import os
from typing import List, Tuple

import flwr as fl


def weighted_average(metrics: List[Tuple[int, dict]]) -> dict:
    total_examples = sum(num_examples for num_examples, _ in metrics)
    if total_examples == 0:
        return {"accuracy": 0.0, "macro_f1": 0.0}

    aggregated: dict[str, float] = {}
    for num_examples, metric in metrics:
        for key, value in metric.items():
            if isinstance(value, (int, float)):
                aggregated[key] = aggregated.get(key, 0.0) + (num_examples * float(value))

    if not aggregated:
        return {"accuracy": 0.0, "macro_f1": 0.0}
    return {key: value / total_examples for key, value in aggregated.items()}


def fit_config(server_round: int) -> dict:
    return {
        "round": server_round,
        "local_epochs": int(os.getenv("FL_LOCAL_EPOCHS", "1")),
        "batch_size": int(os.getenv("FL_BATCH_SIZE", "32")),
        "learning_rate": float(os.getenv("FL_LEARNING_RATE", "0.05")),
    }


def main() -> None:
    server_address = os.getenv("FL_SERVER_ADDRESS", "0.0.0.0:8080")
    num_rounds = int(os.getenv("FL_NUM_ROUNDS", "10"))
    min_available = int(os.getenv("FL_MIN_AVAILABLE_CLIENTS", "2"))
    min_fit = int(os.getenv("FL_MIN_FIT_CLIENTS", str(min_available)))
    min_evaluate = int(os.getenv("FL_MIN_EVALUATE_CLIENTS", str(min_available)))

    strategy = fl.server.strategy.FedAvg(
        min_available_clients=min_available,
        min_fit_clients=min_fit,
        min_evaluate_clients=min_evaluate,
        on_fit_config_fn=fit_config,
        evaluate_metrics_aggregation_fn=weighted_average,
    )

    print(
        f"[FL-SERVER] Starting Flower server at {server_address} "
        f"for {num_rounds} rounds (min clients: {min_available})"
    )

    history = fl.server.start_server(
        server_address=server_address,
        config=fl.server.ServerConfig(num_rounds=num_rounds),
        strategy=strategy,
    )

    print("[FL-SERVER] Training completed.")
    print(f"[FL-SERVER] Metrics history: {history.metrics_distributed}")


if __name__ == "__main__":
    main()
