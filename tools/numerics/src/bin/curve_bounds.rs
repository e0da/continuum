fn main() {
    let result = (|| {
        let path = continuum_numerics::output_argument()?;
        let report = continuum_numerics::curve::report();
        continuum_numerics::write_json_exclusive(&path, &report)?;
        println!(
            "{}",
            serde_json::json!({"qualified":report.qualified,"casesChecked":report.cases_checked,"floatingPointQualified":false,"productionImplemented":false,"performanceMeasured":false})
        );
        Ok::<_, String>(())
    })();
    if let Err(e) = result {
        eprintln!("curve-bounds: {e}");
        std::process::exit(1)
    }
}
