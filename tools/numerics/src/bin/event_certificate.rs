fn main() {
    let result = (|| {
        let path = continuum_numerics::output_argument()?;
        let report = continuum_numerics::event_certificate::report();
        continuum_numerics::write_json_exclusive(&path, &report)?;
        println!(
            "{}",
            serde_json::json!({
                "cases": report.cases, "coordinateClears": report.coordinate_clears,
                "distanceClears": report.distance_clears,
                "distanceOnlyClears": report.distance_only_clears,
                "falseClearsInSampledOracle": report.false_clears_in_sampled_oracle
            })
        );
        Ok::<_, String>(())
    })();
    if let Err(error) = result {
        eprintln!("event-certificate: {error}");
        std::process::exit(1);
    }
}
