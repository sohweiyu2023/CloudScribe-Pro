# Funding-only successor: all request, receipt and accounting history is retained.
function Assert-LAFundingAdjustment($OldScope,$NewScope,$Plan) {
    $reserve=$Plan.FundingAdjustment.Contains('PreviousTaxFxProvisionUsdCents') -or $Plan.FundingAdjustment.Contains('NewTaxFxProvisionUsdCents')
    $fields=@('PreviousCapUsdCents','NewCapUsdCents')
    if($reserve){$fields+=@('PreviousTaxFxProvisionUsdCents','NewTaxFxProvisionUsdCents')}
    Assert-LARecord $Plan.FundingAdjustment $fields
    Assert-LAInteger $Plan.FundingAdjustment.PreviousCapUsdCents 1
    Assert-LAInteger $Plan.FundingAdjustment.NewCapUsdCents 1
    Assert-LAInteger $OldScope.TotalGoogleCapUsdCents 1
    Assert-LAInteger $NewScope.TotalGoogleCapUsdCents 1
    if($Plan.FundingAdjustment.PreviousCapUsdCents -ne $OldScope.TotalGoogleCapUsdCents -or
        $Plan.FundingAdjustment.NewCapUsdCents -ne $NewScope.TotalGoogleCapUsdCents -or
        $NewScope.TotalGoogleCapUsdCents -le $OldScope.TotalGoogleCapUsdCents -or
        $NewScope.SourceApprovalSha256 -cne $Plan.SourceApprovalSha256 -or
        $NewScope.SourceApprovalSha256 -ceq $OldScope.SourceApprovalSha256){throw 'LongAudioFundingFreshApprovalRequired'}
    Assert-LAHash $Plan.SourceApprovalSha256
    if($reserve){
        foreach($value in @($Plan.FundingAdjustment.PreviousTaxFxProvisionUsdCents,$Plan.FundingAdjustment.NewTaxFxProvisionUsdCents,$OldScope.TaxFxProvisionUsdCents,$NewScope.TaxFxProvisionUsdCents)){Assert-LAInteger $value}
        if($Plan.FundingAdjustment.PreviousTaxFxProvisionUsdCents -ne $OldScope.TaxFxProvisionUsdCents -or
            $Plan.FundingAdjustment.NewTaxFxProvisionUsdCents -ne $NewScope.TaxFxProvisionUsdCents -or
            $NewScope.TaxFxProvisionUsdCents -le $OldScope.TaxFxProvisionUsdCents -or
            $NewScope.TaxFxProvisionUsdCents -ge $NewScope.TotalGoogleCapUsdCents){throw 'LongAudioFundingReserveChanged'}
    }
    $before=ConvertFrom-LAJson (ConvertTo-LABytes $OldScope)
    $after=ConvertFrom-LAJson (ConvertTo-LABytes $NewScope)
    foreach($value in @($before,$after)){
        [void]$value.Remove('SourceApprovalSha256')
        [void]$value.Remove('TotalGoogleCapUsdCents')
        if($reserve){[void]$value.Remove('TaxFxProvisionUsdCents')}
    }
    if((Get-LAHash (ConvertTo-LABytes $before)) -cne (Get-LAHash (ConvertTo-LABytes $after))){throw 'LongAudioFundingUnrelatedScopeChange'}
}
